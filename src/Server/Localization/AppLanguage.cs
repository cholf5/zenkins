using System.Globalization;
using Microsoft.Net.Http.Headers;

namespace Zenkins.Server.Localization;

/// <summary>
/// The languages the UI ships in (v1: English and Simplified Chinese), the cookie that remembers a
/// visitor's choice, and the two ways that choice is learned.
/// <para>
/// <b>Cookie contract:</b> only the canonical codes below are accepted, compared with
/// <see cref="StringComparison.Ordinal"/>. Any other value (<c>zh-Hans-CN</c>, <c>ZH-hans</c>,
/// garbage) means "no choice", exactly like <c>ThemePreference</c>, so the server never guesses a
/// variant of a language the UI does not ship.
/// </para>
/// <para>
/// <b>First visit:</b> no cookie means the browser's <c>Accept-Language</c> decides, best match
/// first; anything the UI does not ship (French, Japanese) falls back to English rather than to the
/// server machine's culture - a shared CI server must not answer in its operator's language.
/// </para>
/// </summary>
public static class AppLanguage
{
    /// <summary>The preference cookie. Written by the <c>/language</c> form endpoint only.</summary>
    public const string CookieName = "zenkins-lang";

    /// <summary>Fallback language, and the language the source strings are written in.</summary>
    public const string English = "en";

    /// <summary>Simplified Chinese. BCP-47, so <c>&lt;html lang&gt;</c> is correct as-is.</summary>
    public const string ChineseSimplified = "zh-Hans";

    /// <summary>The choices the switcher offers, in display order, with their own-language names.</summary>
    public static readonly IReadOnlyList<AppLanguageChoice> Choices =
    [
        new(English, "English"),
        new(ChineseSimplified, "简体中文"),
    ];

    /// <summary>Whether <paramref name="code"/> is one of the canonical codes this UI ships.</summary>
    public static bool IsSupported(string? code) => code is English or ChineseSimplified;

    /// <summary>
    /// The language for a request: the remembered cookie when it is canonical, otherwise the best
    /// supported entry of <c>Accept-Language</c>, otherwise English.
    /// </summary>
    public static string Resolve(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var remembered = request.Cookies[CookieName];
        return IsSupported(remembered) ? remembered! : FromAcceptLanguage(request);
    }

    /// <summary>
    /// The best supported language in the request's <c>Accept-Language</c> header. Quality values
    /// order the candidates (<c>q=0</c> means "not acceptable"); a regional tag such as
    /// <c>zh-CN</c> or <c>en-GB</c> selects its shipped base language.
    /// </summary>
    public static string FromAcceptLanguage(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var languages = request.GetTypedHeaders().AcceptLanguage;
        if (languages is null || languages.Count == 0)
        {
            return English;
        }

        foreach (var candidate in languages
                     .Where(candidate => candidate.Value.HasValue)
                     .OrderByDescending(candidate => candidate.Quality ?? 1.0))
        {
            // q=0 is an explicit refusal, not a weaker preference.
            if ((candidate.Quality ?? 1.0) <= 0)
            {
                continue;
            }

            var tag = candidate.Value.Value!;
            if (tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                return ChineseSimplified;
            }

            if (tag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            {
                return English;
            }
        }

        return English;
    }

    /// <summary>
    /// The culture the language's text is formatted with (numbers, <c>string.Format</c> arguments).
    /// Never <see cref="CultureInfo.CurrentCulture"/>: this service answers per request/circuit, and
    /// an interactive circuit must not inherit whatever culture the machine happens to run under.
    /// </summary>
    public static CultureInfo Culture(string language) =>
        CultureInfo.GetCultureInfo(IsSupported(language) ? language : English);
}

/// <summary>One language the switcher offers: its canonical code and its own-language name.</summary>
/// <param name="Code">The canonical code, used in the cookie and in <c>&lt;html lang&gt;</c>.</param>
/// <param name="NativeName">The name shown in the switcher, never translated.</param>
public sealed record AppLanguageChoice(string Code, string NativeName);
