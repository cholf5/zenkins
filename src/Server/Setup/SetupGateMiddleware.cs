using System.Data.Common;
using Zenkins.Server.Auth;

namespace Zenkins.Server.Setup;

/// <summary>
/// The first-run gate (card F-044): one door into a deployment with no accounts, and no way back
/// through it afterwards.
/// <para>
/// While the bootstrap marker is absent, the account pages send the browser to <c>/setup</c>; the
/// wizard is the only path that can create the Owner, so a visitor cannot accidentally skip it by
/// using the old registration form and silently create an organization named
/// <c>Starter Organization</c>. Once the marker exists, <c>/setup</c> and <c>/setup/owner</c> are
/// gone (to the dashboard when signed in, to sign-in otherwise) and the account pages work normally
/// for later registrations. <c>/setup/worker</c> stays reachable: it is the same capability as the
/// Workers page, and the wizard's last step may be continued after a reload.
/// </para>
/// <para>
/// When the first-run state cannot be read at all - the schema behind the binaries, the database
/// unreachable - every gated path is sent to <c>/setup</c>, which is the page that explains the
/// problem. That is the one case where this middleware intentionally does not answer 302 for a
/// healthy deployment: the alternative would be an exception page for the operator's very first
/// request.
/// </para>
/// </summary>
public sealed class SetupGateMiddleware(RequestDelegate next)
{
    private const string SetupWelcomePath = "/setup";
    private const string SetupOwnerPath = "/setup/owner";
    private const string SetupWorkerPath = "/setup/worker";
    private const string LoginPath = "/account/login";
    private const string RegisterPath = "/account/register";

    /// <summary>Runs the gate for the five paths it owns and passes everything else straight through.</summary>
    public async Task InvokeAsync(
        HttpContext context,
        IBootstrapService bootstrap,
        ILogger<SetupGateMiddleware> logger)
    {
        var path = NormalizedPath(context.Request);
        if (path is not (SetupWelcomePath or SetupOwnerPath or SetupWorkerPath or LoginPath or RegisterPath))
        {
            await next(context);
            return;
        }

        bool completed;
        try
        {
            completed = await bootstrap.HasCompletedAsync(context.RequestAborted);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            logger.LogWarning(
                exception,
                "The first-run state could not be read; sending the entry pages to /setup to explain it.");

            if (path == SetupWelcomePath)
            {
                await next(context);
            }
            else
            {
                context.Response.Redirect(SetupWelcomePath);
            }

            return;
        }

        var signedIn = context.User.Identity?.IsAuthenticated == true;

        switch (path)
        {
            case SetupWelcomePath or SetupOwnerPath when completed:
                context.Response.Redirect(signedIn ? "/" : LoginPath);
                return;

            case SetupWorkerPath when !completed:
                context.Response.Redirect(SetupWelcomePath);
                return;

            case LoginPath or RegisterPath when !completed:
                context.Response.Redirect(SetupWelcomePath);
                return;

            default:
                await next(context);
                return;
        }
    }

    /// <summary>
    /// The path without a trailing slash and lower-cased, so <c>/setup/</c> and <c>/SETUP</c> are the
    /// same door as <c>/setup</c> - routing is case-insensitive, and a gate that only knows the exact
    /// casing would let a differently spelled URL reach the page it guards.
    /// </summary>
    private static string NormalizedPath(HttpRequest request)
    {
        var value = request.Path.Value ?? string.Empty;
        return (value.Length > 1 ? value.TrimEnd('/') : value).ToLowerInvariant();
    }
}
