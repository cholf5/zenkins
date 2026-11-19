using System.Globalization;

namespace Zenkins.Server.Localization;

/// <summary>
/// The UI's text. Injected into every Razor component as <c>L10n</c> (see
/// <c>_Imports.razor</c>), so a page writes its copy as an indexer lookup on the English string and
/// an interpolated sentence as an indexer lookup with <see cref="string.Format(string, object?[])"/>
/// arguments.
/// <para>
/// The English string is the key: English is the source language, and an untranslated key renders
/// the English text rather than a symbolic name. Placeholders are <see cref="string.Format(string, object?[])"/>
/// slots, formatted with the <em>language's</em> culture, never the ambient one - an interactive
/// circuit can outlive the request that started it, and must not inherit a machine locale.
/// </para>
/// </summary>
public sealed class Localizer(LanguageContext language, TranslationCatalog catalog)
{
    /// <summary>The current language code (for example <c>zh-Hans</c>).</summary>
    public string Language => language.Current;

    /// <summary>The culture to format values in for this language.</summary>
    public CultureInfo Culture => AppLanguage.Culture(Language);

    /// <summary>Whether the current language is <paramref name="code"/>.</summary>
    public bool Is(string code) => string.Equals(Language, code, StringComparison.Ordinal);

    /// <summary>The translated string for <paramref name="english"/>.</summary>
    public string Translate(string english) => catalog.Translate(english, Language);

    /// <summary>A translated string without arguments.</summary>
    public string this[string english] => Translate(english);

    /// <summary>A translated string with <see cref="string.Format(string, object?[])"/> arguments.</summary>
    public string this[string english, params object?[] arguments] =>
        string.Format(Culture, Translate(english), arguments);

    /// <summary>
    /// The right English plural for <paramref name="count"/> (or its translation when the language
    /// does not inflect: Chinese maps both keys to the same text). Both arguments are keys, so both
    /// are covered by the catalogue.
    /// </summary>
    public string Plural(long count, string one, string many) => Translate(count == 1 ? one : many);
}
