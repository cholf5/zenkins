using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Zenkins.Server.Webhooks;

/// <summary>
/// HTTP endpoints for card F-017: the delivery endpoint a git host calls
/// (<c>POST /webhooks/{webhookId}</c>) and the project settings forms that manage webhooks.
/// <para>
/// <b>The delivery endpoint is anonymous by construction, authenticated by a signature.</b> It sits
/// outside the cookie-auth fallback exactly like the worker API: it never looks at
/// <c>HttpContext.User</c>, accepts no cookie as authority, and takes its tenant from the webhook row it
/// resolves. That is also why CSRF is not a concern here - a request carries no ambient credential, so a
/// cross-site page cannot make an honest browser deliver anything; the shared secret is the whole
/// authorization. Antiforgery stays on for the settings forms, which <em>do</em> ride a cookie.
/// </para>
/// <para>
/// <b>Status codes.</b> <c>202</c> with a small JSON body for accepted, ignored and duplicate deliveries;
/// a bodyless <c>401</c> for a bad or absent signature; a bodyless <c>404</c> for an unknown, disabled or
/// foreign id; <c>400</c> for a payload that is not a push; <c>413</c> when the body exceeds the cap
/// (counted while reading); <c>429</c> with <c>Retry-After</c> when the webhook's budget is spent. None
/// of the rejections names a tenant, a project, an expected signature or the scheme that was tried.
/// </para>
/// </summary>
public static class WebhookEndpoints
{
    /// <summary>The delivery URL prefix; the only variable part is the opaque webhook id.</summary>
    public const string DeliveryPathPrefix = "/webhooks";

    /// <summary>The settings forms' prefix (the project page's own path + the feature's segment).</summary>
    public const string ManagementPathPrefix = "/projects/{projectId:guid}/webhooks";

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Delivery: deliberately NOT RequireAuthorization. The signature is the authentication; a session
        // cookie must never authorize a delivery (and does not).
        endpoints.MapPost($"{DeliveryPathPrefix}/{{webhookId:guid}}", ReceiveAsync);

        // Management: cookie + antiforgery, like the other form endpoints.
        var management = endpoints.MapGroup(ManagementPathPrefix).RequireAuthorization();
        management.MapPost("/create", CreateAsync);
        management.MapPost("/{webhookId:guid}/rotate", RotateAsync);
        management.MapPost("/{webhookId:guid}/enable", EnableAsync);
        management.MapPost("/{webhookId:guid}/disable", DisableAsync);
        management.MapPost("/{webhookId:guid}/delete", DeleteAsync);

        return endpoints;
    }

    // ---------------------------------------------------------------------
    // Delivery
    // ---------------------------------------------------------------------

    private static async Task<IResult> ReceiveAsync(
        Guid webhookId,
        HttpContext httpContext,
        WebhookDeliveryService deliveries,
        WebhookOptions options,
        WebhookRateLimiter rateLimiter,
        WebhookDeliveryTelemetry telemetry)
    {
        var cancellationToken = httpContext.RequestAborted;

        // 1. The budget, FIRST (reviews m5 and the 429-vs-404 ruling). It is keyed by the raw id from the
        //    URL - not by the delivery, not by whether the id exists - so a flood of never-seen ids is
        //    counted exactly like a real webhook's traffic, and an oversized-body flood cannot get past the
        //    429 by never reaching the parse step. Nothing is read from the request body before this point.
        if (!rateLimiter.TryAcquire(webhookId, out var retryAfter))
        {
            httpContext.Response.Headers.RetryAfter = Math.Max(
                    1,
                    (int)Math.Ceiling(retryAfter.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        // 2. The raw body, read once, before anything is bound or parsed: the signature is over these exact
        //    bytes. The cap is counted while reading, so an oversized body is refused without being held -
        //    the telemetry counter is what makes "not held" observable instead of a claim.
        var body = await ReadBodyAsync(
            httpContext.Request,
            options.MaxBodyBytes,
            telemetry,
            cancellationToken);
        if (body is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var result = await deliveries.HandleAsync(
            webhookId,
            ReadHeader(httpContext.Request, WebhookSignature.HubSignatureHeader),
            ReadHeader(httpContext.Request, WebhookSignature.GitLabTokenHeader),
            WebhookDeliveryService.ReadDeliveryId(ReadDeliveryIdHeaders(httpContext.Request)),
            body,
            cancellationToken);

        switch (result.Status)
        {
            // Every miss is this one answer: same status, no body, no content type. An unknown id, a
            // disabled webhook and another tenant's id are indistinguishable.
            case WebhookDeliveryStatus.NotFound:
                return Results.StatusCode(StatusCodes.Status404NotFound);

            // A signature failure says nothing: not which scheme was tried, not what was expected.
            case WebhookDeliveryStatus.InvalidSignature:
                return Results.StatusCode(StatusCodes.Status401Unauthorized);

            case WebhookDeliveryStatus.InvalidPayload:
                return Results.Json(
                    new WebhookErrorResponse(result.ErrorCode ?? WebhookPayloadErrorCodes.PayloadInvalid),
                    statusCode: StatusCodes.Status400BadRequest);

            default:
                // The closed vocabulary, named in full so a client reads the set itself and not a
                // remembered subset: `queued`, `ignored:branch`, `ignored:not-branch`, `ignored:delete`,
                // `ignored:no-repository`, `ignored:branch-too-long`, `awaiting-observation`,
                // `no-pipeline`, `project-disabled` and `queue-full` (cards F-028/F-029: the project's
                // own gate refused the push - recorded, and still a 2xx) are the members of
                // `WebhookOutcomes.All`; `duplicate` is the status a replay answers with, and the record
                // still carries the original outcome and, when it queued one, the original run id. A fact
                // holds this list equal to the set, so the sentence cannot fall behind it.
                return Results.Json(new WebhookDeliveryResponse(
                    Status: result.Status == WebhookDeliveryStatus.Duplicate
                        ? "duplicate"
                        : result.Outcome ?? WebhookOutcomes.Queued,
                    Outcome: result.Outcome ?? string.Empty,
                    RunId: result.RunId,
                    Branch: result.Branch,
                    Commit: result.Commit),
                    statusCode: StatusCodes.Status202Accepted);
        }
    }

    /// <summary>
    /// Reads the body into a bounded buffer, refusing as soon as the cap is crossed. Returns
    /// <see langword="null"/> when the body is too large: the caller answers 413 and never holds the
    /// whole payload.
    /// <para>
    /// <b>The cap is enforced while reading, not after.</b> A declared <c>Content-Length</c> over the cap is
    /// refused before the stream is touched, and every other shape (including a chunked body with no length
    /// at all) is read in 16 KiB chunks and abandoned the moment the running total crosses the cap - the
    /// remaining bytes are never buffered. The telemetry counts what was actually buffered, so "an oversized
    /// body is not held in memory" is an observation a test can make, not a property to be trusted.
    /// </para>
    /// </summary>
    private static async Task<byte[]?> ReadBodyAsync(
        HttpRequest request,
        int maxBytes,
        WebhookDeliveryTelemetry telemetry,
        CancellationToken cancellationToken)
    {
        // A declared oversized length is refused without touching the stream at all.
        if (request.ContentLength is { } declared && declared > maxBytes)
        {
            telemetry.RecordOversizedRejection(bufferedBytes: 0);
            return null;
        }

        var buffer = new byte[16 * 1024];
        using var body = new MemoryStream();
        var total = 0;

        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                // Stop reading here: the remaining bytes are never buffered, so an oversized or endless
                // body costs the cap plus one chunk, not the request's size.
                telemetry.RecordOversizedRejection(body.Length);
                return null;
            }

            body.Write(buffer, 0, read);
        }

        telemetry.RecordBuffered(total);
        return body.ToArray();
    }

    private static string? ReadHeader(HttpRequest request, string name) =>
        request.Headers.TryGetValue(name, out var values) ? values.ToString() : null;

    private static Dictionary<string, string?> ReadDeliveryIdHeaders(HttpRequest request)
    {
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in WebhookDeliveryService.DeliveryIdHeaders)
        {
            headers[name] = ReadHeader(request, name);
        }

        return headers;
    }

    // ---------------------------------------------------------------------
    // Management
    // ---------------------------------------------------------------------

    private static async Task<IResult> CreateAsync(
        Guid projectId,
        [FromForm] CreateWebhookForm form,
        HttpContext httpContext,
        WebhookService webhooks)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // F-019: a body whose fields all fail to bind arrives as null, not as an empty form. The
        // service, not a guard, validates it, so the answer stays the settings tab's own PRG error
        // (?webhookError=name-required) and no webhook is created.
        var result = await webhooks.CreateAsync(
            userId,
            projectId,
            form?.ToInput() ?? EmptyInput(),
            httpContext.RequestAborted);

        return result.Status switch
        {
            // The secret is shown once: a standalone document instead of a redirect, because a redirect
            // would put the only copy into the URL (history, proxies, access logs).
            WebhookManagementStatus.Success => ShowSecretOnce(
                "Webhook created",
                projectId,
                result.WebhookId!.Value,
                result.PlaintextSecret!),

            WebhookManagementStatus.Forbidden => Forbidden(result),
            WebhookManagementStatus.NotFound => Results.NotFound(),
            _ => ValidationRedirect(projectId, result),
        };
    }

    private static async Task<IResult> RotateAsync(
        Guid projectId,
        Guid webhookId,
        [FromForm] RotateWebhookForm form,
        HttpContext httpContext,
        WebhookService webhooks)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await webhooks.RotateAsync(userId, projectId, webhookId, httpContext.RequestAborted);
        return result.Status switch
        {
            WebhookManagementStatus.Success => ShowSecretOnce(
                "Webhook secret rotated",
                projectId,
                result.WebhookId!.Value,
                result.PlaintextSecret!),
            WebhookManagementStatus.Forbidden => Forbidden(result),
            _ => Results.NotFound(),
        };
    }

    private static async Task<IResult> EnableAsync(
        Guid projectId,
        Guid webhookId,
        [FromForm] ToggleWebhookForm form,
        HttpContext httpContext,
        WebhookService webhooks) =>
        await SetEnabledAsync(projectId, webhookId, enabled: true, httpContext, webhooks);

    private static async Task<IResult> DisableAsync(
        Guid projectId,
        Guid webhookId,
        [FromForm] ToggleWebhookForm form,
        HttpContext httpContext,
        WebhookService webhooks) =>
        await SetEnabledAsync(projectId, webhookId, enabled: false, httpContext, webhooks);

    private static async Task<IResult> SetEnabledAsync(
        Guid projectId,
        Guid webhookId,
        bool enabled,
        HttpContext httpContext,
        WebhookService webhooks)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await webhooks.SetEnabledAsync(
            userId,
            projectId,
            webhookId,
            enabled,
            httpContext.RequestAborted);

        return result.Status switch
        {
            // D-003: the webhook forms live on the settings tab, so the PRG returns there (the notice
            // renders next to the form). The F-017-pinned behaviour - the 302, the notice code, the
            // service's rules - is unchanged.
            WebhookManagementStatus.Success =>
                Results.Redirect($"/projects/{projectId}/settings?notice={(enabled ? "webhook-enabled" : "webhook-disabled")}"),
            WebhookManagementStatus.Forbidden => Forbidden(result),
            _ => Results.NotFound(),
        };
    }

    private static async Task<IResult> DeleteAsync(
        Guid projectId,
        Guid webhookId,
        [FromForm] DeleteWebhookForm form,
        HttpContext httpContext,
        WebhookService webhooks)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await webhooks.DeleteAsync(userId, projectId, webhookId, httpContext.RequestAborted);
        return result.Status switch
        {
            // D-003: PRG back to the settings tab, where the webhook list lives.
            WebhookManagementStatus.Success =>
                Results.Redirect($"/projects/{projectId}/settings?notice=webhook-deleted"),
            WebhookManagementStatus.Forbidden => Forbidden(result),
            _ => Results.NotFound(),
        };
    }

    private static IResult Forbidden(WebhookManagementResult result) =>
        Results.Text(
            result.Message ?? "You are not allowed to perform this action.",
            "text/plain",
            statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// PRG back to the settings tab's webhook form with the first field error (D-003 moved the form
    /// there; the F-017-pinned <c>webhookError</c> code is unchanged). Only the submitted name and
    /// filter travel in the query - the form has no secret field at all, so there is nothing secret
    /// to leak.
    /// </summary>
    private static IResult ValidationRedirect(Guid projectId, WebhookManagementResult result)
    {
        var code = result.FieldErrors?.FirstOrDefault()?.Code ?? WebhookErrorCodes.NameRequired;
        var query = QueryString.Create(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["webhookError"] = code,
        });

        return Results.Redirect($"/projects/{projectId}/settings{query}");
    }

    /// <summary>
    /// The one response that ever carries a plaintext secret. It is a standalone HTML document rather
    /// than a redirect, and it is explicitly uncacheable, so the only copy lives in this response body.
    /// </summary>
    private static IResult ShowSecretOnce(
        string title,
        Guid projectId,
        Guid webhookId,
        string plaintextSecret)
    {
        // The endpoint URL is part of the document because it is the other half of the configuration the
        // operator has to paste into the git host.
        var deliveryPath = $"{DeliveryPathPrefix}/{webhookId:D}";
        var html = $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head><meta charset="utf-8"><title>{{title}} · Zenkins</title></head>
            <body>
              <h1>{{title}}</h1>
              <p>Copy the secret now - it is shown only once and cannot be retrieved again.</p>
              <p>Secret: <code id="webhook-secret-plaintext">{{System.Net.WebUtility.HtmlEncode(plaintextSecret)}}</code></p>
              <p>Delivery URL: <code id="webhook-endpoint-path">{{System.Net.WebUtility.HtmlEncode(deliveryPath)}}</code></p>
              <p>Configure the git host with that URL, the JSON content type and this secret
                 (GitHub: "Secret"; GitLab: "Secret token").</p>
              <p><a href="/projects/{{projectId:D}}/settings">Back to the project settings</a></p>
            </body>
            </html>
            """;

        return new UncachedHtmlResult(html);
    }

    private static string? FindUserId(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>
    /// The stand-in for a form that bound null (a POST carrying no field the model knows): empty
    /// values, so the service's validation - not a null reference - answers the request (F-019).
    /// </summary>
    private static WebhookInput EmptyInput() => new(Name: null, BranchFilter: null, RequiredTags: null);

    /// <summary>
    /// A 200 HTML response carrying a secret: always <c>no-store</c>, so neither an intermediary nor the
    /// browser keeps a copy of the only plaintext the product ever shows.
    /// </summary>
    private sealed class UncachedHtmlResult(string html) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            httpContext.Response.ContentType = "text/html; charset=utf-8";
            return httpContext.Response.WriteAsync(html, httpContext.RequestAborted);
        }
    }

    /// <summary>Create form: the name, the branch filter and the run's tags. No secret field: the
    /// server generates it and shows it exactly once.</summary>
    public sealed class CreateWebhookForm
    {
        public string? Name { get; set; }

        public string? BranchFilter { get; set; }

        public string? RequiredTags { get; set; }

        public WebhookInput ToInput() => new(Name, BranchFilter, RequiredTags);
    }

    /// <summary>Rotate form: empty on purpose - the route ids are the target and the secret is
    /// generated server-side.</summary>
    public sealed class RotateWebhookForm
    {
    }

    /// <summary>Enable/disable form: empty on purpose (see <see cref="RotateWebhookForm"/>).</summary>
    public sealed class ToggleWebhookForm
    {
    }

    /// <summary>Delete form: empty on purpose; the confirmation is the page's.</summary>
    public sealed class DeleteWebhookForm
    {
    }

    /// <summary>
    /// The answer of an accepted delivery. <see cref="Status"/> is `duplicate` for a replay, and otherwise
    /// one of the members of <see cref="WebhookOutcomes.All"/> - the list is written out once, at the
    /// response site above, and a fact holds it equal to the set; <see cref="Outcome"/> is the stored
    /// disposition the same delivery would answer with again.
    /// </summary>
    public sealed record WebhookDeliveryResponse(
        string Status,
        string Outcome,
        Guid? RunId,
        string? Branch,
        string? Commit);

    /// <summary>A payload-side 400: one stable code, no tenant or project detail.</summary>
    public sealed record WebhookErrorResponse(string Error);
}
