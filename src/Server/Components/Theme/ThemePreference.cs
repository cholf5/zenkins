namespace Zenkins.Server.Components.Theme;

/// <summary>
/// The server-readable theme preference. The theme toggle writes this cookie from the browser
/// (see <c>wwwroot/js/theme-persistence.js</c>) so the initial HTML can be rendered with the
/// correct <c>.dark</c> class instead of flashing the wrong theme until Blazor starts.
/// </summary>
/// <remarks>
/// The contract is exact: only the canonical <see cref="DarkValue"/> / <see cref="LightValue"/>
/// strings are accepted, compared with <see cref="StringComparison.Ordinal"/>. Any other value
/// (<c>Dark</c>, <c>DARK</c>, garbage) means "no preference", exactly as the pre-paint script
/// treats it, so both sides fall back to the same resolver instead of disagreeing about the
/// first paint. Do not relax this to a case-insensitive compare without doing the same in
/// <c>theme-persistence.js</c>.
/// </remarks>
public static class ThemePreference
{
    public const string CookieName = "zenkins-theme";
    public const string DarkValue = "dark";
    public const string LightValue = "light";

    public static bool IsDark(HttpContext? httpContext) =>
        string.Equals(
            httpContext?.Request.Cookies[CookieName],
            DarkValue,
            StringComparison.Ordinal);
}
