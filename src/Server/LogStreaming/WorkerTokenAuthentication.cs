using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zenkins.Server.Workers;

namespace Zenkins.Server.LogStreaming;

/// <summary>Constants for the worker-token authentication scheme (F-008's worker upstream hub).</summary>
public static class WorkerTokenAuthenticationDefaults
{
    /// <summary>Distinct from the Identity cookie scheme: the hub only ever accepts a worker token.</summary>
    public const string AuthenticationScheme = "Zenkins.WorkerToken";
}

public sealed class WorkerTokenAuthenticationOptions : AuthenticationSchemeOptions
{
}

/// <summary>
/// Authenticates the worker API's bearer token (<c>zwrk_…</c>) for the worker upstream SignalR hub,
/// using the exact credential machinery of the F-004/F-006 HTTP endpoints:
/// <see cref="WorkerService.AuthenticateWorkerAsync"/> resolves the hash through the system scope,
/// the token row selects the tenant, and every failure (missing, unknown, burned, revoked) is a
/// bare 401 with no tenant detail.
/// <para>
/// SignalR clients present the token either as <c>Authorization: Bearer …</c> (negotiate, long
/// polling) or as the <c>access_token</c> query string (the WebSocket handshake, which browsers
/// and non-browser clients cannot send with a header). Both are read here, mirroring what the
/// JwtBearer handler does for the same reason.
/// </para>
/// <para>
/// The claims carry the worker id and organization id so hub methods can authorize without a
/// second lookup; the plaintext token never rides a claim. The connection still re-validates the
/// token per hub method (revocation recheck, same discipline as every other worker API call).
/// </para>
/// </summary>
public sealed class WorkerTokenAuthenticationHandler(
    IOptionsMonitor<WorkerTokenAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    WorkerService workers)
    : AuthenticationHandler<WorkerTokenAuthenticationOptions>(options, logger, encoder)
{
    /// <summary>Reads the SignalR-presented credential from the header or the query string.</summary>
    public static string? ReadToken(HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return null;
        }

        var header = httpContext.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var headerToken = header["Bearer ".Length..].Trim();
            if (headerToken.Length > 0)
            {
                return headerToken;
            }
        }

        var queryToken = httpContext.Request.Query["access_token"].ToString();
        return queryToken.Length > 0 ? queryToken : null;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadToken(Context);
        if (token is null)
        {
            // No credential presented: NoResult lets the challenge answer a bare 401, exactly like
            // the worker HTTP API's tokenless path (never a 400-vs-401 probing difference).
            return AuthenticateResult.NoResult();
        }

        var workerToken = await workers.AuthenticateWorkerAsync(token, Context.RequestAborted);
        if (workerToken is null || workerToken.WorkerId is not { } workerId)
        {
            return AuthenticateResult.Fail("invalid worker token");
        }

        var claims = new[]
        {
            new Claim(ZenkinsClaims.WorkerId, workerId.ToString()),
            new Claim(ZenkinsClaims.OrganizationId, workerToken.OrganizationId.ToString()),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // House style for every worker-auth failure: bare 401, no body, no tenant detail.
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}

/// <summary>Claim type constants shared by the two F-008 hub authentication schemes.</summary>
public static class ZenkinsClaims
{
    /// <summary>The authenticated worker's id (worker upstream hub).</summary>
    public const string WorkerId = "zenkins:worker_id";

    /// <summary>The organization the credential belongs to (both hubs).</summary>
    public const string OrganizationId = "zenkins:organization_id";
}
