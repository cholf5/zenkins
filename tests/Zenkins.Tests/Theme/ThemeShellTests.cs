using System.Net;
using Xunit;
using Zenkins.Server.Components.Theme;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Theme;

/// <summary>
/// Card D-007's HTTP tests: the app bar theme switcher and the two pieces of infrastructure it
/// needs, driven through the real pages and static web assets.
/// <list type="bullet">
/// <item>The <c>zenkins-theme</c> cookie still drives the server-rendered dark class (the d-001
/// contract, unchanged by D-007), and the cookie's verdict is echoed to the package's pre-paint
/// script as <c>data-default-dark</c> so a cookie-dark browser with localStorage blocked is not
/// flipped to the OS light preference.</item>
/// <item>The palette stylesheet (css/themes.css) and the pre-paint restore script (js/theme-init.js)
/// are referenced by the head and really served.</item>
/// <item>The shell renders the theme switcher trigger and no longer the bare dark-mode toggle.</item>
/// </list>
/// </summary>
[Trait("Category", "Theme")]
public sealed class ThemeShellTests
{
    private const string ThemesCssPath = "_content/BlazorBlueprint.Components/css/themes.css";
    private const string ThemeInitPath = "_content/BlazorBlueprint.Components/js/theme-init.js";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_theme_cookie_renders_dark_and_tells_theme_init_so()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = new AuthTestHost();

        // The account pages are static SSR, so the contract is observable without signing in.
        using var dark = host.CreateClient();
        dark.DefaultRequestHeaders.Add("Cookie", $"{ThemePreference.CookieName}=dark");
        var darkHtml = await AuthTestHost.GetStringAsync(dark, "/account/login");

        Assert.Contains("<html lang=\"en\" class=\"dark\"", darkHtml, StringComparison.Ordinal);
        Assert.Contains($"{ThemeInitPath}\" data-default-dark=\"true\"", darkHtml, StringComparison.Ordinal);

        using var plain = host.CreateClient();
        var lightHtml = await AuthTestHost.GetStringAsync(plain, "/account/login");

        Assert.Contains("<html lang=\"en\">", lightHtml, StringComparison.Ordinal);
        Assert.Contains($"{ThemeInitPath}\" data-default-dark=\"false\"", lightHtml, StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_head_wires_the_palette_stylesheet_and_the_pre_paint_script_and_serves_both()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = new AuthTestHost();
        using var client = await SignInAsync(host);

        var page = await AuthTestHost.GetStringAsync(client, "/");

        Assert.Contains(ThemesCssPath, page, StringComparison.Ordinal);
        Assert.Contains(ThemeInitPath, page, StringComparison.Ordinal);

        // The referenced assets must exist, or the switcher's colors would silently never render.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/{ThemesCssPath}", CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/{ThemeInitPath}", CancellationToken)).StatusCode);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_app_bar_offers_the_theme_switcher_and_not_the_bare_dark_toggle()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = new AuthTestHost();
        using var client = await SignInAsync(host);

        var page = await AuthTestHost.GetStringAsync(client, "/");

        // The switcher's trigger: an icon-only ghost button that opens the theme popover.
        var trigger = page.IndexOf("aria-label=\"Customize theme\"", StringComparison.Ordinal);
        Assert.True(trigger >= 0, "The app bar did not render the theme switcher trigger.");
        Assert.Contains(
            "aria-haspopup=\"true\"",
            page[Math.Max(0, trigger - 600)..(trigger + 20)],
            StringComparison.Ordinal);

        // The old one-button toggle is gone (the package's default aria-labels for it, d-001).
        Assert.DoesNotContain("Switch to dark mode", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Switch to light mode", page, StringComparison.Ordinal);
    }

    private static async Task<HttpClient> SignInAsync(AuthTestHost host)
    {
        var client = host.CreateClient();
        var email = $"theme-{Guid.NewGuid():N}@example.test";
        var registered = await AuthTestHost.RegisterAsync(client, email);
        Assert.True(
            (int)registered.StatusCode is 302 or 303,
            $"Registering the theme test user failed with {(int)registered.StatusCode}.");
        await AuthTestHost.LoginAsync(client, email);
        return client;
    }
}
