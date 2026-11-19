using System.Text.Json;

namespace Zenkins.Server.Localization;

/// <summary>
/// The shipped translations, keyed by the English source string. English is not stored: it is the
/// key itself, so a missing entry falls back to readable English instead of a resource name, and a
/// new string needs no second file to compile.
/// <para>
/// Two embedded JSON catalogues, merged here:
/// <list type="bullet">
/// <item><c>Translations.zh-Hans.json</c> - the UI's own copy. Every key must appear in the source
/// (an <c>L10n</c> lookup or a validation attribute) and every source string must appear here;
/// <c>TranslationCatalogTests</c> enforces both directions.</item>
/// <item><c>Translations.framework.zh-Hans.json</c> - text the framework produces and the UI shows
/// verbatim (ASP.NET Identity's password and email messages). It cannot be found by scanning the
/// source, so it is kept apart and only checked for shape (placeholders, non-empty values).</item>
/// </list>
/// A missing or malformed catalogue throws at startup instead of quietly rendering English to every
/// Chinese visitor.
/// </para>
/// </summary>
public sealed class TranslationCatalog
{
    /// <summary>The embedded resource name of the UI copy catalogue.</summary>
    public const string UiResourceName = "Zenkins.Server.Localization.Translations.zh-Hans.json";

    /// <summary>The embedded resource name of the framework-produced message catalogue.</summary>
    public const string FrameworkResourceName =
        "Zenkins.Server.Localization.Translations.framework.zh-Hans.json";

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IReadOnlyDictionary<string, string> _chinese;
    private readonly IReadOnlyDictionary<string, string> _uiChinese;
    private readonly IReadOnlyDictionary<string, string> _frameworkChinese;

    /// <summary>Loads the embedded catalogues.</summary>
    public TranslationCatalog()
        : this(Load(UiResourceName), Load(FrameworkResourceName))
    {
    }

    /// <summary>Uses explicit catalogues (tests only).</summary>
    internal TranslationCatalog(
        IReadOnlyDictionary<string, string> uiChinese,
        IReadOnlyDictionary<string, string> frameworkChinese)
    {
        _uiChinese = uiChinese;
        _frameworkChinese = frameworkChinese;

        var merged = new Dictionary<string, string>(uiChinese, StringComparer.Ordinal);
        foreach (var (key, value) in frameworkChinese)
        {
            if (!merged.TryAdd(key, value))
            {
                throw new InvalidOperationException(
                    $"'{key}' appears in both translation catalogues. Keep each English key in exactly one "
                    + "of Translations.zh-Hans.json and Translations.framework.zh-Hans.json.");
            }
        }

        _chinese = merged;
    }

    /// <summary>Every Chinese entry, merged, for the localizer.</summary>
    internal IReadOnlyDictionary<string, string> Chinese => _chinese;

    /// <summary>The UI copy entries, the ones the source-coverage test checks.</summary>
    internal IReadOnlyDictionary<string, string> UiChinese => _uiChinese;

    /// <summary>The framework-produced message entries.</summary>
    internal IReadOnlyDictionary<string, string> FrameworkChinese => _frameworkChinese;

    /// <summary>
    /// The translation of <paramref name="english"/> in <paramref name="language"/>, or the English
    /// string itself when the language is English or the entry is missing.
    /// </summary>
    public string Translate(string english, string language)
    {
        ArgumentNullException.ThrowIfNull(english);

        return language == AppLanguage.ChineseSimplified && _chinese.TryGetValue(english, out var chinese)
            ? chinese
            : english;
    }

    private static IReadOnlyDictionary<string, string> Load(string resourceName)
    {
        var assembly = typeof(TranslationCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"The translation catalogue '{resourceName}' is missing from {assembly.GetName().Name}. "
                + "It must be an EmbeddedResource of the Server project (see Zenkins.Server.csproj).");

        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, jsonOptions)
            ?? throw new InvalidOperationException($"The translation catalogue '{resourceName}' is empty.");

        foreach (var (key, value) in entries)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"The translation catalogue '{resourceName}' has an empty key or value for '{key}'.");
            }
        }

        return entries;
    }
}
