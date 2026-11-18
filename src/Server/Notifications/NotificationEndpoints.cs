using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Zenkins.Server.Notifications;

/// <summary>
/// Card F-039's settings-tab forms: create an endpoint, enable/disable it, rotate its secret, remove it, and send one
/// test delivery. The same shape as every other project mutation (form-bound, so <c>UseAntiforgery</c> validates the
/// token; PRG with a stable code; a foreign or missing id is a bodyless 404); the service enforces Owner-only writes
/// and validates the URL, the secret and the events.
/// </summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/projects/{projectId:guid}/notifications").RequireAuthorization();
        group.MapPost("/create", CreateAsync);
        group.MapPost("/{endpointId:guid}/delete", DeleteAsync);
        group.MapPost("/{endpointId:guid}/enable", EnableAsync);
        group.MapPost("/{endpointId:guid}/disable", DisableAsync);
        group.MapPost("/{endpointId:guid}/rotate", RotateAsync);
        group.MapPost("/{endpointId:guid}/test", TestAsync);
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        Guid projectId,
        [FromForm] NotificationEndpointForm form,
        HttpContext httpContext,
        NotificationService notifications)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // F-019's rule in miniature: a body whose fields all failed to bind is the page's PRG refusal, not a crash.
        var result = await notifications.CreateAsync(
            userId,
            projectId,
            new NotificationEndpointInput(
                form?.Url,
                form?.Secret,
                form?.Events,
                Enabled: !string.Equals(form?.Enabled, "false", StringComparison.Ordinal),
                AllowInsecureHttp: string.Equals(form?.AllowInsecureHttp, "true", StringComparison.Ordinal)),
            httpContext.RequestAborted);

        return Map(result, projectId, "notification-added", httpContext);
    }

    private static Task<IResult> DeleteAsync(
        Guid projectId,
        Guid endpointId,
        [FromForm] NotificationToggleForm form,
        HttpContext httpContext,
        NotificationService notifications)
    {
        _ = form;
        return MutateAsync(
            projectId,
            httpContext,
            userId => notifications.DeleteAsync(userId, projectId, endpointId, httpContext.RequestAborted),
            "notification-deleted");
    }

    private static Task<IResult> EnableAsync(
        Guid projectId,
        Guid endpointId,
        [FromForm] NotificationToggleForm form,
        HttpContext httpContext,
        NotificationService notifications)
    {
        _ = form;
        return MutateAsync(
            projectId,
            httpContext,
            userId => notifications.SetEnabledAsync(userId, projectId, endpointId, true, httpContext.RequestAborted),
            "notification-enabled");
    }

    private static Task<IResult> DisableAsync(
        Guid projectId,
        Guid endpointId,
        [FromForm] NotificationToggleForm form,
        HttpContext httpContext,
        NotificationService notifications)
    {
        _ = form;
        return MutateAsync(
            projectId,
            httpContext,
            userId => notifications.SetEnabledAsync(userId, projectId, endpointId, false, httpContext.RequestAborted),
            "notification-disabled");
    }

    private static async Task<IResult> RotateAsync(
        Guid projectId,
        Guid endpointId,
        [FromForm] NotificationSecretForm form,
        HttpContext httpContext,
        NotificationService notifications)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await notifications.RotateSecretAsync(
            userId,
            projectId,
            endpointId,
            form?.Secret,
            httpContext.RequestAborted);
        return Map(result, projectId, "notification-rotated", httpContext);
    }

    /// <summary>
    /// Sends one test delivery now and reports what happened. Synchronous on purpose: the operator pressed a button
    /// and is waiting for the answer, bounded by the sender's own timeout - the no-blocking rule is about builds.
    /// </summary>
    private static async Task<IResult> TestAsync(
        Guid projectId,
        Guid endpointId,
        [FromForm] NotificationToggleForm form,
        HttpContext httpContext,
        NotificationService notifications)
    {
        _ = form;
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await notifications.SendTestAsync(
            userId,
            projectId,
            endpointId,
            httpContext.RequestAborted);

        return result.Status switch
        {
            NotificationMutationStatus.NotFound => Results.NotFound(),
            NotificationMutationStatus.Forbidden => Results.Text(
                result.Message ?? "You are not allowed to send a test notification.",
                "text/plain",
                statusCode: StatusCodes.Status403Forbidden),
            _ when result.Delivered => Results.Redirect(
                $"/projects/{projectId}/settings?notice=notification-test-delivered&notifyStatus={result.StatusCode}"),
            _ => Results.Redirect(
                $"/projects/{projectId}/settings?notifyTest=failed&notifyDetail={Uri.EscapeDataString(result.Message ?? string.Empty)}"),
        };
    }

    private static async Task<IResult> MutateAsync(
        Guid projectId,
        HttpContext httpContext,
        Func<string, Task<NotificationMutationResult>> mutation,
        string notice)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        return Map(await mutation(userId), projectId, notice, httpContext);
    }

    /// <summary>
    /// The one place a mutation's outcome becomes an HTTP answer: success is a PRG to the tab that renders the card, a
    /// refusal comes back with its stable code, and a foreign or missing id is a bodyless 404.
    /// </summary>
    private static IResult Map(
        NotificationMutationResult result,
        Guid projectId,
        string notice,
        HttpContext httpContext)
    {
        _ = httpContext;
        return result.Status switch
        {
            NotificationMutationStatus.Success => Results.Redirect($"/projects/{projectId}/settings?notice={notice}"),
            NotificationMutationStatus.NotFound => Results.NotFound(),
            NotificationMutationStatus.Forbidden => Results.Text(
                result.Message ?? "You are not allowed to change notification endpoints.",
                "text/plain",
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Redirect(
                $"/projects/{projectId}/settings?notifyError={Uri.EscapeDataString(result.ErrorCode ?? "notify-invalid")}"),
        };
    }

    private static string? FindUserId(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>
    /// Card F-039's create form: the receiver URL, the shared secret, the subscribed events (checkboxes), the enabled
    /// flag and the plain-http opt-in. Nothing is bound as a number or a bool, so a typo reaches the service's
    /// validation and comes back as a stable code instead of a binding failure.
    /// </summary>
    public sealed class NotificationEndpointForm
    {
        public string? Url { get; set; }

        /// <summary>The shared secret, typed by the operator for their own receiver. Never echoed back.</summary>
        public string? Secret { get; set; }

        /// <summary>The checked events; empty means "nothing subscribed" and is refused.</summary>
        public string[]? Events { get; set; }

        /// <summary><c>false</c> from the checkbox's hidden twin; absent means the endpoint starts enabled.</summary>
        public string? Enabled { get; set; }

        /// <summary><c>true</c> when the operator ticked "allow plain http".</summary>
        public string? AllowInsecureHttp { get; set; }
    }

    /// <summary>Card F-039's secret rotation form: the new shared secret and nothing else.</summary>
    public sealed class NotificationSecretForm
    {
        public string? Secret { get; set; }
    }

    /// <summary>Empty on purpose: the parameter is what makes the toggle/delete/test POSTs form-bound (antiforgery).</summary>
    public sealed class NotificationToggleForm
    {
    }
}
