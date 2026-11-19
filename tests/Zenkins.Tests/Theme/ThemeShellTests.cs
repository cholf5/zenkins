using Xunit;
using Zenkins.Server.Components.Theme;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Theme;

/// <summary>
/// Card D-007's HTTP tests: the app bar theme switcher and the two pieces of infrastructure it
/// needs, driven through the real pages and static web assets. Tightened by Q-013 after D-007's
/// independent review showed the first version would stay green through the very regressions it
/// claimed to prevent: a script moved out of <c>&lt;head&gt;</c>, an emptied palette stylesheet, a
/// dropped <c>AdditionalAttributes</c>.
/// <list type="bullet">
/// <item>The <c>zenkins-theme</c> cookie still drives the server-rendered dark class (the d-001
/// contract), and its verdict is echoed to the package's pre-paint script as
/// <c>data-default-dark</c> so a cookie-dark browser with localStorage blocked is not flipped to
/// the OS light preference. The cookie contract is exact: only <c>dark</c> / <c>light</c> count.</item>
/// <item>The head references the four stylesheets and the two blocking scripts <em>in order</em>,
/// and the two theme assets are asserted by body, not just by status code.</item>
/// <item>The shell renders the theme switcher trigger — its own start tag carries the wiring and
/// the ghost square classes, and it holds no text.</item>
/// </list>
/// </summary>
[Trait("Category", "Theme")]
public sealed class ThemeShellTests
{
    private const string ThemesCssPath = "_content/BlazorBlueprint.Components/css/themes.css";
    private const string ThemeInitPath = "_content/BlazorBlueprint.Components/js/theme-init.js";
    private const string AppThemePath = "styles/theme.css";
    private const string FrameworkCssPath = "_content/BlazorBlueprint.Components/blazorblueprint.css";
    private const string AppCssPath = "app.css";
    private const string PersistenceScriptPath = "js/theme-persistence.js";

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_theme_cookie_is_the_only_dark_source_and_its_contract_is_exact()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = new AuthTestHost();

        // The wizard is static SSR like the account pages (F-044), so the contract is observable
        // without signing in.
        using var dark = host.CreateClient();
        dark.DefaultRequestHeaders.Add("Cookie", $"{ThemePreference.CookieName}={ThemePreference.DarkValue}");
        AssertShellTheme(await AuthTestHost.GetStringAsync(dark, "/setup"), isDark: true);

        using var plain = host.CreateClient();
        AssertShellTheme(await AuthTestHost.GetStringAsync(plain, "/setup"), isDark: false);

        // Q-013: the negatives. `light` is the other canonical value and must render light even
        // though the package's own resolver would fall back to the OS preference from it; `Dark`
        // differs only in case, so it is "no preference" to ThemePreference (an Ordinal compare)
        // and must not become a dark shell behind the bridge's back.
        using var light = host.CreateClient();
        light.DefaultRequestHeaders.Add("Cookie", $"{ThemePreference.CookieName}={ThemePreference.LightValue}");
        AssertShellTheme(await AuthTestHost.GetStringAsync(light, "/setup"), isDark: false);

        using var wrongCase = host.CreateClient();
        wrongCase.DefaultRequestHeaders.Add("Cookie", $"{ThemePreference.CookieName}=Dark");
        AssertShellTheme(await AuthTestHost.GetStringAsync(wrongCase, "/setup"), isDark: false);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_head_loads_the_theme_cascade_and_the_pre_paint_scripts_in_order()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = new AuthTestHost();
        using var client = await SignInAsync(host);

        var page = await AuthTestHost.GetStringAsync(client, "/");
        var headEnd = page.IndexOf("</head>", StringComparison.Ordinal);
        Assert.True(headEnd > 0, "The page has no </head>; the shell did not render its document.");

        // The cascade order decides which token wins, so it is asserted, not just presence:
        // the app's own tokens first, then the package's defaults, then its palette (which keys
        // off the data attributes and must beat the defaults), then the app's document styles.
        var appTheme = IndexInHead(page, AppThemePath, headEnd);
        var framework = IndexInHead(page, FrameworkCssPath, headEnd);
        var palette = IndexInHead(page, ThemesCssPath, headEnd);
        var appCss = IndexInHead(page, AppCssPath, headEnd);

        Assert.True(appTheme < framework, $"styles/theme.css must load before blazorblueprint.css ({appTheme} vs {framework}).");
        Assert.True(framework < palette, $"blazorblueprint.css must load before css/themes.css ({framework} vs {palette}).");
        Assert.True(palette < appCss, $"css/themes.css must load before app.css ({palette} vs {appCss}).");

        // Both scripts are classic and blocking and must run before the document ends: theme-init
        // after the bridge that repairs/seeds `bb-theme`, and both before </head>, or the first
        // paint is the wrong theme (the flash D-007 exists to prevent).
        var persistence = IndexInHead(page, PersistenceScriptPath, headEnd);
        var init = IndexInHead(page, ThemeInitPath, headEnd);
        Assert.True(persistence < init, $"js/theme-persistence.js must run before theme-init.js ({persistence} vs {init}).");

        // The referenced assets must really carry the definitions the wiring promises. A 200 with an
        // empty body is exactly the regression the status-only version of this test missed.
        var paletteCss = await AuthTestHost.GetStringAsync(client, "/" + ThemesCssPath);
        Assert.Contains("[data-base-color=\"zinc\"]", paletteCss, StringComparison.Ordinal);
        Assert.Contains("[data-primary-color=\"violet\"]", paletteCss, StringComparison.Ordinal);

        var initJs = await AuthTestHost.GetStringAsync(client, "/" + ThemeInitPath);
        Assert.Contains("getAttribute('data-default-dark')", initJs, StringComparison.Ordinal);
        Assert.Contains("defaultDark === 'true'", initJs, StringComparison.Ordinal);
        Assert.Contains("defaultDark === 'false'", initJs, StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_app_bar_offers_an_icon_only_theme_switcher_trigger_wired_to_the_popover()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = new AuthTestHost();
        using var client = await SignInAsync(host);

        var page = await AuthTestHost.GetStringAsync(client, "/");

        // Q-013: locate the trigger's OWN start tag first. The first version of this test looked
        // back 600 characters from the label, which would also have matched a wrapper's attributes
        // — and `aria-label="Customize theme"` is the package's default, so a dropped
        // AdditionalAttributes would still have passed.
        var label = page.IndexOf("aria-label=\"Customize theme\"", StringComparison.Ordinal);
        Assert.True(label >= 0, "The app bar did not render the theme switcher trigger.");

        var trigger = StartTagAround(page, label);
        Assert.StartsWith("<button", trigger, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aria-haspopup=\"true\"", trigger, StringComparison.Ordinal);

        // MainLayout's TriggerClass: ghost variant, square icon button.
        Assert.Contains("bb:h-9 bb:w-9 bb:p-0", trigger, StringComparison.Ordinal);

        // Icon-only: no text node inside the trigger at all (the old assertion was "the previous
        // label is absent", which says nothing about what the button now renders).
        var inner = ElementContent(page, label);
        Assert.True(
            string.IsNullOrWhiteSpace(StripTags(inner)),
            $"The theme switcher trigger is not icon-only; its content reads '{StripTags(inner)}'.");

        // The old one-button toggle is gone (the package's default aria-labels for it, d-001).
        Assert.DoesNotContain("Switch to dark mode", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Switch to light mode", page, StringComparison.Ordinal);
    }

    /// <summary>The shell renders <c>class="dark"</c> and echoes the same verdict to theme-init.</summary>
    private static void AssertShellTheme(string html, bool isDark)
    {
        var htmlOpen = html.IndexOf("<html", StringComparison.Ordinal);
        Assert.True(htmlOpen >= 0, "The page did not start with <html>.");
        var htmlTag = html[htmlOpen..(html.IndexOf('>', htmlOpen) + 1)];

        Assert.Equal(isDark, htmlTag.Contains("class=\"dark\"", StringComparison.Ordinal));
        Assert.Contains(
            $"{ThemeInitPath}\" data-default-dark=\"{(isDark ? "true" : "false")}\"",
            html,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Where a head-referenced asset appears, asserting it is inside <c>&lt;head&gt;</c> at all —
    /// the positional comparisons below are meaningless for an asset that moved to the body.
    /// </summary>
    private static int IndexInHead(string page, string needle, int headEnd)
    {
        var index = page.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"The head does not reference '{needle}'.");
        Assert.True(index < headEnd, $"'{needle}' is referenced after </head> (offset {index}, head ends at {headEnd}).");
        return index;
    }

    /// <summary>
    /// The markup of the element whose attribute list contains <paramref name="index"/>, from its
    /// <c>&lt;</c> to its closing <c>&gt;</c> (quote-aware, so an attribute value containing
    /// <c>&gt;</c> cannot end the tag early).
    /// </summary>
    private static string StartTagAround(string html, int index)
    {
        var start = index;
        while (start > 0 && html[start] != '<')
        {
            start--;
        }

        return html[start..(TagEnd(html, start) + 1)];
    }

    /// <summary>
    /// The element's inner HTML, found by matching its own tag name so a sibling's close tag cannot
    /// be mistaken for it.
    /// </summary>
    private static string ElementContent(string html, int index)
    {
        var start = index;
        while (start > 0 && html[start] != '<')
        {
            start--;
        }

        var end = TagEnd(html, start);
        var nameEnd = start + 1;
        while (nameEnd < html.Length && (char.IsLetterOrDigit(html[nameEnd]) || html[nameEnd] == '-'))
        {
            nameEnd++;
        }

        var name = html[(start + 1)..nameEnd];
        var depth = 1;
        var cursor = end + 1;
        while (cursor < html.Length)
        {
            var next = html.IndexOf('<', cursor);
            if (next < 0)
            {
                break;
            }

            if (html.AsSpan(next).StartsWith($"</{name}", StringComparison.OrdinalIgnoreCase))
            {
                depth--;
                if (depth == 0)
                {
                    return html[(end + 1)..next];
                }
            }
            else if (html.AsSpan(next).StartsWith($"<{name}", StringComparison.OrdinalIgnoreCase))
            {
                depth++;
            }

            cursor = next + 1;
        }

        return html[(end + 1)..];
    }

    /// <summary>The index of the <c>&gt;</c> that closes the tag starting at <paramref name="start"/>.</summary>
    private static int TagEnd(string html, int start)
    {
        char? quote = null;
        for (var index = start; index < html.Length; index++)
        {
            var character = html[index];
            if (quote is not null)
            {
                if (character == quote)
                {
                    quote = null;
                }
            }
            else if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (character == '>')
            {
                return index;
            }
        }

        return html.Length - 1;
    }

    /// <summary>Everything that is not markup — i.e. the text nodes of an HTML fragment.</summary>
    private static string StripTags(string html)
    {
        var text = new System.Text.StringBuilder(html.Length);
        var inTag = false;
        foreach (var character in html)
        {
            if (character == '<')
            {
                inTag = true;
            }
            else if (character == '>')
            {
                inTag = false;
            }
            else if (!inTag)
            {
                text.Append(character);
            }
        }

        return text.ToString();
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
