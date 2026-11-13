using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Zenkins.Server.Workers;

namespace Zenkins.Server.Secrets;

/// <summary>
/// Real POST endpoints for secret create, rotate and delete (card F-012, PRD §3.8).
/// <para>
/// <b>There is no GET endpoint here and no value-returning one.</b> The only read surface is the
/// <c>/secrets</c> page, which renders names and timestamps. That property is asserted by enumerating
/// the endpoint table in a test (<c>SecretRouteShapeTests</c>), not by convention: any new route under
/// <c>/secrets</c> that is not one of the three mutations - or any mutation that accepts a method
/// besides POST - fails that test.
/// </para>
/// <para>
/// <b>Form-bound like F-002/F-003</b>, so <c>UseAntiforgery</c> validates every call automatically and
/// a direct POST from curl hits the same server-side role check. The route id is authoritative; the
/// form models carry no secret id.
/// </para>
/// <para>
/// <b>Outcomes.</b> Success is a PRG redirect with <c>?notice=</c>; a foreign or missing id is a
/// bodyless <c>404</c>; a Member's write is a <c>403</c> with no state change; validation failures
/// redirect back with a stable <c>?error=</c> code. The submitted <em>value</em> is never echoed into a
/// redirect (query strings are logged), only the name.
/// </para>
/// </summary>
public static class SecretEndpoints
{
    /// <summary>The page that owns the create/rotate/delete forms and every redirect target.</summary>
    public const string ListPath = "/secrets";

    /// <summary>
    /// The one worker-authenticated value route (card F-015): <c>secret()</c> delivery. It is deliberately
    /// not a user route - the caller presents a worker token that is executing the run named in the path,
    /// and the name travels in the body so a secret <em>name</em> never lands in a URL or an access log.
    /// </summary>
    public const string WorkerFetchPath = "/api/workers/runs/{runId:guid}/secrets";

    /// <summary>Maps the three secret mutation endpoints (and the run-scoped worker fetch). All user
    /// routes require authentication; the worker fetch authenticates with a bearer token instead.</summary>
    public static IEndpointRouteBuilder MapSecretEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/secrets").RequireAuthorization();

        group.MapPost("/create", CreateAsync);
        group.MapPost("/{secretId:guid}/rotate", RotateAsync);
        group.MapPost("/{secretId:guid}/delete", DeleteAsync);

        // F-015: the run-scoped fetch a worker makes while a step that references a secret is about to
        // start. Exempt from cookie auth by design (the token selects the tenant and proves the run), like
        // the other /api/workers routes.
        endpoints.MapPost(WorkerFetchPath, FetchRunSecretAsync);

        return endpoints;
    }

    private static async Task<IResult> FetchRunSecretAsync(
        Guid runId,
        [FromBody] RunSecretRequest request,
        HttpContext httpContext,
        RunSecretService secrets)
    {
        var result = await secrets.FetchValueForRunAsync(
            WorkerEndpoints.BearerToken(httpContext),
            runId,
            request.Name,
            httpContext.RequestAborted);

        return result.Status switch
        {
            // The body is a secret: explicitly uncacheable, so neither an intermediary nor a client can
            // keep a copy of it.
            RunSecretFetchStatus.Success => WithNoStore(
                httpContext,
                Results.Json(new RunSecretResponse(result.Name!, result.Value!))),

            // A bad token is authentication, answered before any run or name lookup: the 401 reveals only
            // that the credential was not accepted, never whether a run or a name exists.
            RunSecretFetchStatus.InvalidToken => Results.Unauthorized(),

            // Deliberately bodyless and identical for every miss (unknown run, foreign tenant, another
            // worker's run, unknown name): nothing to distinguish, nothing to probe.
            _ => Results.NotFound(),
        };
    }

    private static async Task<IResult> CreateAsync(
        [FromForm] CreateSecretForm form,
        HttpContext httpContext,
        SecretService secrets)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await secrets.CreateAsync(userId, form.ToInput(), httpContext.RequestAborted);
        return result.Status switch
        {
            SecretMutationStatus.Success => Results.Redirect($"{ListPath}?notice=created"),
            SecretMutationStatus.Forbidden => Forbidden(result),
            SecretMutationStatus.NotFound => Results.NotFound(),
            _ => ValidationRedirect(result, form.Name),
        };
    }

    private static async Task<IResult> RotateAsync(
        Guid secretId,
        [FromForm] RotateSecretForm form,
        HttpContext httpContext,
        SecretService secrets)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await secrets.RotateAsync(userId, secretId, form.ToInput(), httpContext.RequestAborted);
        return result.Status switch
        {
            SecretMutationStatus.Success => Results.Redirect($"{ListPath}?notice=rotated"),

            // Authorization and existence are hard status codes, never a friendly redirect: the UI is
            // not the enforcement point, so a Member's direct POST must see the 403.
            SecretMutationStatus.Forbidden => Forbidden(result),
            SecretMutationStatus.NotFound => Results.NotFound(),
            _ => ValidationRedirect(result, name: null),
        };
    }

    private static async Task<IResult> DeleteAsync(
        Guid secretId,
        [FromForm] DeleteSecretForm form,
        HttpContext httpContext,
        SecretService secrets)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await secrets.DeleteAsync(userId, secretId, httpContext.RequestAborted);
        return result.Status switch
        {
            SecretMutationStatus.Success => Results.Redirect($"{ListPath}?notice=deleted"),
            SecretMutationStatus.Forbidden => Forbidden(result),
            _ => Results.NotFound(),
        };
    }

    private static IResult Forbidden(SecretMutationResult result) =>
        Results.Text(
            result.Message ?? "You are not allowed to perform this action.",
            "text/plain",
            statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// PRG back to the page with the first field error. Only the submitted <em>name</em> travels in the
    /// query, so neither the value nor the ciphertext can end up in a URL, a redirect header or an
    /// access log.
    /// </summary>
    private static IResult ValidationRedirect(SecretMutationResult result, string? name)
    {
        var code = result.FieldErrors?.FirstOrDefault()?.Code ?? SecretErrorCodes.NameInvalid;
        var query = QueryString.Create(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["error"] = code,
            ["name"] = name,
        });

        return Results.Redirect($"{ListPath}{query}");
    }

    private static string? FindUserId(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>
    /// Marks a response as carrying a secret (card F-015): no intermediary and no client may keep a copy.
    /// </summary>
    private static IResult WithNoStore(HttpContext httpContext, IResult result)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        return result;
    }

    /// <summary>Create form: the name identity plus the value. No id: the server assigns it.</summary>
    public sealed class CreateSecretForm
    {
        public string? Name { get; set; }

        public string? Value { get; set; }

        public SecretInput ToInput() => new(Name, Value);
    }

    /// <summary>
    /// The worker fetch body (card F-015): the name <c>secret()</c> was called with. No value and no
    /// organization id: the token selects the tenant and the path selects the run.
    /// </summary>
    public sealed class RunSecretRequest
    {
        public string? Name { get; set; }
    }

    /// <summary>The worker fetch's answer: the name it asked for and the decrypted value.</summary>
    public sealed record RunSecretResponse(string Name, string Value);

    /// <summary>
    /// Rotate form: the new value only. There is deliberately no <c>Name</c> field - a rotation cannot
    /// rename (that is delete + create), and the route id is the target.
    /// </summary>
    public sealed class RotateSecretForm
    {
        public string? Value { get; set; }

        public SecretInput ToInput() => new(Name: null, Value);
    }

    /// <summary>
    /// Empty on purpose: the parameter exists so the delete POST is form-bound and therefore covered by
    /// <c>UseAntiforgery</c>. The page's confirmation step is the confirmation.
    /// </summary>
    public sealed class DeleteSecretForm
    {
    }
}
