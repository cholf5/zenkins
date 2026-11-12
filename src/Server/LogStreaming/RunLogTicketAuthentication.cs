using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Zenkins.Server.LogStreaming;

/// <summary>Constants for the run-log subscription ticket scheme (F-008's browser hub).</summary>
public static class RunLogTicketAuthenticationDefaults
{
    /// <summary>Distinct from the Identity cookie scheme: the browser hub only accepts a ticket
    /// minted for the signed-in user, because the circuit's server-side <c>HubConnection</c>
    /// (same process) cannot replay a cookie it does not hold.</summary>
    public const string AuthenticationScheme = "Zenkins.RunLogTicket";
}

public sealed class RunLogTicketAuthenticationOptions : AuthenticationSchemeOptions
{
}

/// <summary>
/// Authenticates a run-log subscription ticket (minted by the run detail page for the signed-in
/// user's session) on the browser-side SignalR hub. The ticket is a data-protected, self-expiring
/// value binding the user id and organization id; the hub re-validates the organization membership
/// when a group is joined, so a ticket from before a membership revocation buys nothing.
/// <para>
/// Like the worker scheme, the ticket arrives as <c>Authorization: Bearer …</c> or the
/// <c>access_token</c> query string (WebSocket handshake). Every failure is a bare 401.
/// </para>
/// </summary>
public sealed class RunLogTicketAuthenticationHandler(
    IOptionsMonitor<RunLogTicketAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    RunLogAccess runLogAccess)
    : AuthenticationHandler<RunLogTicketAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = WorkerTokenAuthenticationHandler.ReadToken(Context);
        if (token is null)
        {
            return AuthenticateResult.NoResult();
        }

        var ticket = runLogAccess.ValidateTicket(token);
        if (ticket is null)
        {
            return AuthenticateResult.Fail("invalid or expired run-log ticket");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, ticket.UserId),
            new Claim(ZenkinsClaims.OrganizationId, ticket.OrganizationId.ToString()),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
