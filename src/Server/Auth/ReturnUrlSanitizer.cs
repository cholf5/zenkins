namespace Zenkins.Server.Auth;

/// <summary>
/// Keeps login redirects inside this application. A <c>ReturnUrl</c> comes from the query string,
/// so it is attacker-controlled: only a site-relative path is accepted, which rules out absolute
/// URLs and protocol-relative <c>//evil.example</c> redirects.
/// </summary>
public static class ReturnUrlSanitizer
{
    /// <summary>The requested path when it is a safe local path, otherwise <paramref name="fallback"/>.</summary>
    public static string Sanitize(string? returnUrl, string fallback = "/")
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return fallback;
        }

        // Relative ("projects") would resolve against the login page; "//host" and "/\host" are
        // treated as protocol-relative by browsers, so both are rejected.
        if (!returnUrl.StartsWith('/')
            || returnUrl.StartsWith("//", StringComparison.Ordinal)
            || returnUrl.StartsWith("/\\", StringComparison.Ordinal))
        {
            return fallback;
        }

        // A decoded query value can contain CR/LF; reflecting it into a redirect would be header
        // injection. No legitimate in-app path needs a control character.
        if (returnUrl.Any(char.IsControl))
        {
            return fallback;
        }

        return returnUrl;
    }

    /// <summary>The login URL that returns to <paramref name="currentRelativePath"/> after signing in.</summary>
    public static string BuildLoginUrl(string currentRelativePath) =>
        $"{IdentitySetup.LoginPath}?returnUrl={Uri.EscapeDataString(Sanitize(currentRelativePath))}";
}
