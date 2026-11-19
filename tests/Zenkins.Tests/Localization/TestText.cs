using Microsoft.AspNetCore.Http;
using Zenkins.Server.Localization;

namespace Zenkins.Tests.Localization;

/// <summary>
/// A localizer pinned to a language for tests that read UI copy without a request: English for the
/// assertions the product has always made, Chinese for the ones that only exist in a translation.
/// </summary>
internal static class TestText
{
    public static Localizer English { get; } = Create(AppLanguage.English);

    public static Localizer Chinese { get; } = Create(AppLanguage.ChineseSimplified);

    private static Localizer Create(string language)
    {
        var context = new LanguageContext(new HttpContextAccessor());
        context.Set(language);
        return new Localizer(context, new TranslationCatalog());
    }
}
