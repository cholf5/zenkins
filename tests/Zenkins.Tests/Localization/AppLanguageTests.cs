using Microsoft.AspNetCore.Http;
using Xunit;
using Zenkins.Server.Localization;

namespace Zenkins.Tests.Localization;

/// <summary>
/// Card F-043's resolution rules, without a host: the cookie contract is exact, a regional
/// <c>Accept-Language</c> picks its shipped base language, quality values order the candidates, and
/// anything the UI does not ship falls back to English - never to the server machine's culture.
/// </summary>
[Trait("Category", "Localization")]
public sealed class AppLanguageTests
{
    [Fact]
    public void Only_the_canonical_codes_are_supported()
    {
        Assert.True(AppLanguage.IsSupported("en"));
        Assert.True(AppLanguage.IsSupported("zh-Hans"));

        // Variants the UI does not ship are "no choice", not a near miss.
        Assert.False(AppLanguage.IsSupported("zh"));
        Assert.False(AppLanguage.IsSupported("zh-Hans-CN"));
        Assert.False(AppLanguage.IsSupported("zh-hans"));
        Assert.False(AppLanguage.IsSupported("EN"));
        Assert.False(AppLanguage.IsSupported(""));
        Assert.False(AppLanguage.IsSupported(null));
    }

    [Fact]
    public void The_cookie_wins_over_accept_language_and_must_be_canonical()
    {
        Assert.Equal(AppLanguage.ChineseSimplified, Resolve("zh-Hans", "en-US"));
        Assert.Equal(AppLanguage.English, Resolve("en", "zh-CN"));

        // A non-canonical cookie is ignored: the header decides, exactly as if no cookie existed.
        Assert.Equal(AppLanguage.ChineseSimplified, Resolve("zh-Hans-CN", "zh-CN"));
        Assert.Equal(AppLanguage.English, Resolve("zh-cn", "en-US"));
    }

    [Theory]
    [InlineData("zh-CN,zh;q=0.9,en;q=0.8", AppLanguage.ChineseSimplified)]
    [InlineData("zh-Hans", AppLanguage.ChineseSimplified)]
    [InlineData("en-GB,en;q=0.9", AppLanguage.English)]
    [InlineData("en-US,zh;q=0.9", AppLanguage.English)]
    [InlineData("fr-FR,fr;q=0.9", AppLanguage.English)]
    [InlineData("fr-FR,zh-CN;q=0.9", AppLanguage.ChineseSimplified)]
    [InlineData("", AppLanguage.English)]
    public void Accept_language_selects_the_best_supported_language(string header, string expected) =>
        Assert.Equal(expected, FromHeader(header));

    [Fact]
    public void A_refused_language_is_not_a_preference() =>
        // zh is explicitly unacceptable, so the next candidate decides.
        Assert.Equal(AppLanguage.English, FromHeader("zh;q=0,en;q=0.5"));

    [Fact]
    public void The_culture_follows_the_language_not_the_machine()
    {
        Assert.Equal("zh-Hans", AppLanguage.Culture(AppLanguage.ChineseSimplified).Name);
        Assert.Equal("en", AppLanguage.Culture(AppLanguage.English).Name);

        // An unsupported code cannot smuggle in another culture.
        Assert.Equal("en", AppLanguage.Culture("de-DE").Name);
    }

    private static string Resolve(string? cookie, string? acceptLanguage)
    {
        var context = new DefaultHttpContext();
        if (cookie is not null)
        {
            context.Request.Headers.Cookie = $"{AppLanguage.CookieName}={cookie}";
        }

        if (acceptLanguage is not null)
        {
            context.Request.Headers.AcceptLanguage = acceptLanguage;
        }

        return AppLanguage.Resolve(context.Request);
    }

    private static string FromHeader(string header)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.AcceptLanguage = header;
        return AppLanguage.FromAcceptLanguage(context.Request);
    }
}
