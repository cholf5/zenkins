using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Zenkins.Server.Auth;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Server.Workers;

/// <summary>
/// HTTP endpoints for card F-004: the token-authenticated worker API under
/// <c>/api/workers/*</c> and the Owner-only registration-token form endpoints under
/// <c>/workers/*</c>.
/// <para>
/// <b>The worker API is exempt from cookie authentication by design.</b> The endpoints never look at
/// <c>HttpContext.User</c>; the only accepted credential is a plaintext token
/// (<c>Authorization: Bearer zreg_…/zwrk_…</c>), and the token row - never a cookie, URL value or
/// header selected by the client - decides the tenant: <see cref="WorkerService"/> sets it on the
/// scoped <see cref="TenantContext"/> after resolving the hash. Every authentication failure is a
/// bare <c>401</c> with no body and no tenant detail.
/// </para>
/// <para>
/// <b>Management endpoints</b> follow F-002/F-003: authenticated, form-bound (so
/// <c>UseAntiforgery</c> validates the token), Owner-only in the service, PRG with
/// <c>?error=</c>/<c>?notice=</c> - with one deliberate exception: the mint response renders the
/// plaintext token once in a standalone HTML document (200) instead of redirecting, because a
/// redirect would put the only copy of the secret into the URL (browser history, proxy and server
/// logs). The plaintext is never stored server-side, so this response is the only chance to show it.
/// </para>
/// </summary>
public static class WorkerEndpoints
{
    /// <summary>Page that lists workers and (for Owners) registration tokens.</summary>
    public const string PagePath = "/workers";

    public static IEndpointRouteBuilder MapWorkerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Worker API: deliberately NOT RequireAuthorization. Authentication is the token; a session
        // cookie must never authorize a worker action (and does not).
        var api = endpoints.MapGroup("/api/workers");
        api.MapPost("/register", RegisterAsync);
        api.MapPost("/heartbeat", HeartbeatAsync);

        // Owner-only management (cookie + antiforgery, like the other form endpoints).
        var management = endpoints.MapGroup("/workers").RequireAuthorization();
        management.MapPost("/registration-tokens", MintRegistrationTokenAsync);
        management.MapPost("/registration-tokens/{tokenId:guid}/revoke", RevokeRegistrationTokenAsync);

        return endpoints;
    }

    private static async Task<IResult> RegisterAsync(
        [FromBody] WorkerRegistrationRequest request,
        HttpContext httpContext,
        WorkerService workers)
    {
        var token = BearerToken(httpContext);
        var result = await workers.RegisterAsync(
            token,
            request.ToInput(),
            httpContext.RequestAborted);

        return result.Status switch
        {
            WorkerOperationStatus.Success => Results.Json(new WorkerRegistrationResponse(
                result.WorkerId!.Value,
                result.WorkerToken!,
                result.WorkerName!)),
            WorkerOperationStatus.InvalidToken => Unauthorized(),
            WorkerOperationStatus.NameConflict => Error(409, result.ErrorCode!),
            _ => Error(400, result.ErrorCode ?? WorkerErrorCodes.FieldInvalid),
        };
    }

    private static async Task<IResult> HeartbeatAsync(
        [FromBody] WorkerHeartbeatRequest request,
        HttpContext httpContext,
        WorkerService workers)
    {
        var token = BearerToken(httpContext);
        var result = await workers.HeartbeatAsync(
            token,
            request.ToInput(),
            httpContext.RequestAborted);

        return result.Status switch
        {
            WorkerOperationStatus.Success => Results.Json(new WorkerHeartbeatResponse(
                result.WorkerId!.Value,
                result.LastHeartbeatAt!.Value)),
            WorkerOperationStatus.InvalidToken => Unauthorized(),

            // A malformed payload from an authenticated worker is the worker's bug to fix.
            _ => Error(400, WorkerErrorCodes.FieldInvalid),
        };
    }

    private static async Task<IResult> MintRegistrationTokenAsync(
        [FromForm] MintRegistrationTokenForm form,
        HttpContext httpContext,
        WorkerService workers)
    {
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await workers.CreateRegistrationTokenAsync(userId, httpContext.RequestAborted);
        if (result.Status != WorkerOperationStatus.Success)
        {
            return Results.Text(
                result.Message ?? "You are not allowed to create registration tokens.",
                "text/plain",
                statusCode: StatusCodes.Status403Forbidden);
        }

        // Shown exactly once, never stored: this document is the only copy (see class doc).
        return Results.Content(
            $"""
             <!DOCTYPE html>
             <html lang="en">
             <head><meta charset="utf-8"><title>Registration token created · Zenkins</title></head>
             <body>
               <h1>Registration token created</h1>
               <p>Copy this token now - it is shown only once and cannot be retrieved again.</p>
               <p><code id="registration-token-plaintext">{System.Net.WebUtility.HtmlEncode(result.Token)}</code></p>
               <p><a href="/workers">Back to workers</a></p>
             </body>
             </html>
             """,
            "text/html",
            statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> RevokeRegistrationTokenAsync(
        Guid tokenId,
        [FromForm] RevokeRegistrationTokenForm form,
        HttpContext httpContext,
        WorkerService workers)
    {
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await workers.RevokeRegistrationTokenAsync(userId, tokenId, httpContext.RequestAborted);
        return result.Status switch
        {
            WorkerOperationStatus.Success => Results.Redirect($"{PagePath}?notice=token-revoked"),
            WorkerOperationStatus.NotFound => Results.NotFound(),

            // Authorization failures are a hard rejection, never a friendly redirect (F-003 rule).
            WorkerOperationStatus.Forbidden => Results.Text(
                result.Message ?? "You are not allowed to revoke registration tokens.",
                "text/plain",
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Redirect($"{PagePath}?error={result.ErrorCode}"),
        };
    }

    /// <summary>
    /// A bare 401: no body, no challenge header, no tenant detail. Every worker-auth failure looks
    /// exactly like this, whether the token was missing, unknown, burned or revoked.
    /// </summary>
    private static IResult Unauthorized() => Results.Unauthorized();

    private static IResult Error(int statusCode, string code) =>
        Results.Json(new WorkerApiError(code), statusCode: statusCode);

    private static string? BearerToken(HttpContext httpContext)
    {
        var header = httpContext.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header["Bearer ".Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>Empty form binding: the parameter makes the POST form-bound and antiforgery-validated.</summary>
    public sealed class MintRegistrationTokenForm
    {
    }

    public sealed class RevokeRegistrationTokenForm
    {
    }

    public sealed record WorkerRegistrationRequest(
        string? Name,
        string? Os,
        string? Architecture,
        string? Version,
        string? Tags,
        int? CpuCount,
        int? MaxConcurrency)
    {
        public WorkerRegistrationInput ToInput() =>
            new(Name, Os, Architecture, Version, Tags, CpuCount, MaxConcurrency);
    }

    public sealed record WorkerHeartbeatRequest(
        string? Os,
        string? Architecture,
        string? Version,
        string? Tags,
        int? CpuCount,
        int? MaxConcurrency)
    {
        public WorkerHeartbeatInput ToInput() =>
            new(Os, Architecture, Version, Tags, CpuCount, MaxConcurrency);
    }

    public sealed record WorkerRegistrationResponse(Guid WorkerId, string WorkerToken, string WorkerName);

    public sealed record WorkerHeartbeatResponse(Guid WorkerId, DateTimeOffset LastHeartbeatAt);

    public sealed record WorkerApiError(string Error);
}
