namespace Zenkins.Server.Localization;

/// <summary>
/// The language of the current request or interactive circuit.
/// <para>
/// Static SSR (account pages, the shell document, the prerender pass) resolves from the cookie and
/// <c>Accept-Language</c> through <see cref="IHttpContextAccessor"/>. An interactive circuit has no
/// <see cref="HttpContext"/> at all, so its language is handed over from the prerender pass with
/// <c>PersistentComponentState</c> (see <c>Routes.razor</c>): the renderer that serves the circuit
/// calls <see cref="Set"/> before any page renders.
/// </para>
/// <para>
/// A circuit that never received that hand-over (persistent state unavailable) falls back to English
/// instead of the server machine's culture, so a shared CI server cannot leak its operator's locale
/// into someone else's browser.
/// </para>
/// </summary>
public sealed class LanguageContext(IHttpContextAccessor httpContextAccessor)
{
    private string? _language;

    /// <summary>The current language code, always one of <see cref="AppLanguage.IsSupported"/>.</summary>
    public string Current =>
        _language
        ?? (httpContextAccessor.HttpContext is { } http
            ? AppLanguage.Resolve(http.Request)
            : AppLanguage.English);

    /// <summary>
    /// Pins the language for the rest of this request/circuit. Only a supported code is accepted;
    /// anything else leaves the previous value (or the resolved default) in place.
    /// </summary>
    public void Set(string? language)
    {
        if (AppLanguage.IsSupported(language))
        {
            _language = language;
        }
    }
}
