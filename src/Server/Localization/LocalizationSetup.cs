namespace Zenkins.Server.Localization;

/// <summary>
/// Registration for the UI's two languages (v1: English and Simplified Chinese).
/// <para>
/// The catalogue is a singleton (read once, immutable); the request/circuit language and the
/// localizer are scoped, so an interactive circuit keeps the language it was opened with and a
/// language switch - a new document request - gets a new scope.
/// </para>
/// </summary>
public static class LocalizationSetup
{
    /// <summary>Registers the translation catalogue and the scoped language services.</summary>
    public static IServiceCollection AddZenkinsLocalization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<TranslationCatalog>();
        services.AddScoped<LanguageContext>();
        services.AddScoped<Localizer>();
        return services;
    }
}
