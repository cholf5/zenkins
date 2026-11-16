using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Zenkins.Server.LogStreaming;

namespace Zenkins.Server.Api;

/// <summary>The API token authentication scheme (card F-027).</summary>
public static class ApiTokenAuthenticationDefaults
{
    /// <summary>
    /// Deliberately separate from the Identity cookie scheme: a session cookie must never authenticate a
    /// machine-facing call, and a token must never authenticate a page.
    /// </summary>
    public const string AuthenticationScheme = "Zenkins.ApiToken";

    /// <summary>The claim type that carries one granted scope (one claim per scope).</summary>
    public const string ScopeClaimType = "zenkins:api_scope";

    /// <summary>The claim type that carries the token row's id (the rate limiter's bucket key).</summary>
    public const string TokenIdClaimType = "zenkins:api_token_id";

    /// <summary>The claim type that carries the token's label (attribution in a run's cancel reason).</summary>
    public const string TokenNameClaimType = "zenkins:api_token_name";

    /// <summary>Policy names: one per scope, each requiring the API scheme plus its scope claim.</summary>
    public static string PolicyFor(string scope) => "Zenkins.ApiScope." + scope;
}

public sealed class ApiTokenAuthenticationOptions : AuthenticationSchemeOptions
{
}

/// <summary>
/// Authenticates <c>Authorization: Bearer zapi_…</c> for <c>/api/v1/*</c> (card F-027) through
/// <see cref="ApiTokenService.AuthenticateAsync"/>: the token row selects the tenant (the same system
/// scope → row → <c>TenantContext</c> path the worker API uses), and the claims carry the organization,
/// the token id and the scopes - never the plaintext.
/// <para>
/// <b>Every failure is one bare 401.</b> No credential, an unknown token, a revoked one and an expired
/// one are indistinguishable (no body, no challenge detail, no tenant). A valid token missing the
/// route's scope is a 403 from the authorization policy - after authentication, before any handler, so
/// no lookup happens for a caller that may not do the thing.
/// </para>
/// <para>
/// <b>No cookie fallback, by construction:</b> the policies name this scheme only, so the handler runs
/// for <c>/api/v1</c> and a session cookie on the same request changes nothing.
/// </para>
/// </summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<ApiTokenAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiTokenService tokens)
    : AuthenticationHandler<ApiTokenAuthenticationOptions>(options, logger, encoder)
{
    /// <summary>The only credential shape the API accepts: a bearer token in the header.</summary>
    public static string? ReadToken(HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return null;
        }

        var header = httpContext.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header["Bearer ".Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var plaintext = ReadToken(Context);
        if (plaintext is null)
        {
            // No credential presented: NoResult lets the challenge answer a bare 401, exactly like the
            // worker API's tokenless path (never a 400-vs-401 probing difference).
            return AuthenticateResult.NoResult();
        }

        var identity = await tokens.AuthenticateAsync(plaintext, Context.RequestAborted);
        if (identity is null)
        {
            return AuthenticateResult.Fail("invalid API token");
        }

        var claims = new List<Claim>
        {
            new(ApiTokenAuthenticationDefaults.TokenIdClaimType, identity.Id.ToString()),
            new(ApiTokenAuthenticationDefaults.TokenNameClaimType, identity.Name),
            new(ZenkinsClaims.OrganizationId, identity.OrganizationId.ToString()),
        };
        claims.AddRange(identity.Scopes.Select(scope =>
            new Claim(ApiTokenAuthenticationDefaults.ScopeClaimType, scope)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // House style for every auth failure: bare 401, no body, no tenant detail.
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
