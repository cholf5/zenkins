namespace Zenkins.Server.Components.Theme;

/// <summary>
/// The server-readable theme preference. The theme toggle writes this cookie from the browser
/// (see <c>wwwroot/js/theme-persistence.js</c>) so the initial HTML can be rendered with the
/// correct <c>.dark</c> class instead of flashing the wrong theme until Blazor starts.
/// </summary>
public static class ThemePreference
{
    public const string CookieName = "zenkins-theme";
    public const string DarkValue = "dark";
    public const string LightValue = "light";

    public static bool IsDark(HttpContext? httpContext) =>
        string.Equals(
            httpContext?.Request.Cookies[CookieName],
            DarkValue,
            StringComparison.OrdinalIgnoreCase);
}
