using System.Net;
using System.Text.Encodings.Web;
using Xunit;
using Zenkins.Server.Localization;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Localization;

/// <summary>
/// Card F-043's HTTP surface: the document's <c>lang</c> and its copy follow the preference cookie,
/// a first visit follows <c>Accept-Language</c>, the switcher's POST writes an HttpOnly cookie and
/// returns to the same page, and an unknown language is refused without a cookie.
/// <para>
/// The account pages are static SSR, so they prove the request path end to end. The signed-in
/// request after registration proves the same for the shell's prerender pass (the bytes a browser
/// receives before the circuit takes over).
/// </para>
/// </summary>
[Trait("Category", "Localization")]
public sealed class LanguageSwitchTests
{
    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_first_visit_follows_accept_language_and_the_switcher_persists_the_choice()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = new AuthTestHost();
        using var client = host.CreateClient();

        // No cookie: the browser's header decides, best match first.
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        var chinese = Decode(await AuthTestHost.GetStringAsync(client, "/account/login"));
        Assert.Contains("<html lang=\"zh-Hans\"", chinese, StringComparison.Ordinal);
        Assert.Contains("登录", chinese, StringComparison.Ordinal);

        // The switcher is a real POST: it sets the cookie and redirects back to the page it was on.
        var token = AuthTestHost.ExtractAntiforgeryToken(chinese);
        using var switched = await AuthTestHost.PostFormAsync(client, "/language", new Dictionary<string, string>
        {
            ["Language"] = AppLanguage.English,
            ["ReturnUrl"] = "/account/login",
            ["__RequestVerificationToken"] = token,
        });
        Assert.Equal(HttpStatusCode.Redirect, switched.StatusCode);
        Assert.Equal("/account/login", switched.Headers.Location?.OriginalString);

        var languageCookie = FindLanguageCookie(switched);
        Assert.NotNull(languageCookie);
        Assert.Contains($"{AppLanguage.CookieName}={AppLanguage.English}", languageCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", languageCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", languageCookie, StringComparison.OrdinalIgnoreCase);

        // The cookie now wins over the still-Chinese header.
        var english = Decode(await AuthTestHost.GetStringAsync(client, "/account/login"));
        Assert.Contains("<html lang=\"en\"", english, StringComparison.Ordinal);
        Assert.Contains("Sign in", english, StringComparison.Ordinal);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task An_unknown_language_is_refused_and_sets_no_cookie()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = new AuthTestHost();
        using var client = host.CreateClient();

        var page = Decode(await AuthTestHost.GetStringAsync(client, "/account/login"));
        var token = AuthTestHost.ExtractAntiforgeryToken(page);

        using var refused = await AuthTestHost.PostFormAsync(client, "/language", new Dictionary<string, string>
        {
            ["Language"] = "fr",
            ["ReturnUrl"] = "/account/login",
            ["__RequestVerificationToken"] = token,
        });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Null(FindLanguageCookie(refused));
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_switcher_cannot_become_an_open_redirect()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = new AuthTestHost();
        using var client = host.CreateClient();

        var page = Decode(await AuthTestHost.GetStringAsync(client, "/account/login"));
        var token = AuthTestHost.ExtractAntiforgeryToken(page);

        using var switched = await AuthTestHost.PostFormAsync(client, "/language", new Dictionary<string, string>
        {
            ["Language"] = AppLanguage.ChineseSimplified,
            ["ReturnUrl"] = "//evil.example/language",
            ["__RequestVerificationToken"] = token,
        });

        Assert.Equal(HttpStatusCode.Redirect, switched.StatusCode);
        Assert.Equal("/", switched.Headers.Location?.OriginalString);
    }

    [Fact(Timeout = 2 * 60 * 1000)]
    public async Task The_signed_in_shell_is_prerendered_in_the_chosen_language()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        using var host = new AuthTestHost();
        using var client = host.CreateClient();
        using (var registered = await AuthTestHost.RegisterAsync(client, "language-shell@example.com"))
        {
            Assert.Equal(HttpStatusCode.Redirect, registered.StatusCode);
        }

        client.DefaultRequestHeaders.Add("Cookie", $"{AppLanguage.CookieName}={AppLanguage.ChineseSimplified}");
        var home = Decode(await AuthTestHost.GetStringAsync(client, "/"));

        Assert.Contains("<html lang=\"zh-Hans\"", home, StringComparison.Ordinal);
        Assert.Contains("仪表盘", home, StringComparison.Ordinal);
        // The nav was translated, not just the document attribute.
        Assert.Contains("代码片段", home, StringComparison.Ordinal);
    }

    // Blazor's static renderer HTML-encodes non-ASCII copy (&#x767B;), so assertions read the text
    // the browser shows, not the entity spelling.
    private static string Decode(string html) => System.Net.WebUtility.HtmlDecode(html);

    private static string? FindLanguageCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(value => value.StartsWith($"{AppLanguage.CookieName}=", StringComparison.Ordinal))
            : null;
}
