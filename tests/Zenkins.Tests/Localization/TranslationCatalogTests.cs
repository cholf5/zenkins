using System.Text.RegularExpressions;
using Xunit;
using Zenkins.Server.Localization;

namespace Zenkins.Tests.Localization;

/// <summary>
/// Card F-043's catalogue guard, run against the source rather than a hand-kept list: every string
/// the UI asks the localizer for has a Simplified Chinese entry, every entry is still used, and each
/// translation carries exactly the same <see cref="string.Format(string, object?[])"/> placeholders
/// as its English key (reordered is fine, missing or invented is not). A new page string with no
/// translation fails here even though the UI itself would silently fall back to English.
/// </summary>
[Trait("Category", "Localization")]
public sealed class TranslationCatalogTests
{
    private static readonly Regex IndexerKeys = new(
        "L10n\\[\"((?:[^\"\\\\]|\\\\.)*)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PluralCalls = new(
        "L10n\\.Plural\\([^;]*?\\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ErrorMessages = new(
        "ErrorMessage\\s*=\\s*\"((?:[^\"\\\\]|\\\\.)*)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex StringLiterals = new(
        "\"((?:[^\"\\\\]|\\\\.)*)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Placeholders = new(
        "\\{(\\d+)\\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void The_catalogue_loads_and_speaks_chinese()
    {
        var catalog = new TranslationCatalog();

        Assert.NotEmpty(catalog.UiChinese);
        Assert.NotEmpty(catalog.FrameworkChinese);
        Assert.Equal("仪表盘", catalog.Translate("Dashboard", AppLanguage.ChineseSimplified));
        Assert.Equal("Dashboard", catalog.Translate("Dashboard", AppLanguage.English));
        Assert.Equal("Not in the catalogue", catalog.Translate("Not in the catalogue", AppLanguage.ChineseSimplified));
    }

    [Fact]
    public void Every_ui_string_has_a_translation_and_no_translation_is_orphaned()
    {
        var catalog = new TranslationCatalog();
        var used = UsedKeys();

        Assert.NotEmpty(used);

        var untranslated = used.Where(key => !catalog.UiChinese.ContainsKey(key)).Order().ToArray();
        Assert.True(
            untranslated.Length == 0,
            "Add these keys to Localization/Translations.zh-Hans.json:\n" + string.Join("\n", untranslated));

        var orphaned = catalog.UiChinese.Keys.Where(key => !used.Contains(key)).Order().ToArray();
        Assert.True(
            orphaned.Length == 0,
            "These catalogue entries are no longer used:\n" + string.Join("\n", orphaned));
    }

    [Fact]
    public void Translations_keep_the_placeholders_of_their_key()
    {
        var catalog = new TranslationCatalog();

        var mismatches = new List<string>();
        foreach (var (key, translation) in catalog.Chinese)
        {
            var expected = Placeholders.Matches(key).Select(match => match.Groups[1].Value).Order().ToArray();
            var actual = Placeholders.Matches(translation).Select(match => match.Groups[1].Value).Order().ToArray();
            if (!expected.SequenceEqual(actual))
            {
                mismatches.Add($"'{key}' expects [{string.Join(", ", expected)}] but '{translation}' has [{string.Join(", ", actual)}]");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    /// <summary>
    /// Every English key the UI passes to the localizer, read from the Razor and C# sources. Both
    /// call shapes are covered: the indexer (<c>L10n["..."]</c>) and <c>Plural</c>'s two keys.
    /// </summary>
    private static HashSet<string> UsedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles())
        {
            var source = File.ReadAllText(file);

            foreach (Match match in IndexerKeys.Matches(source))
            {
                keys.Add(Unescape(match.Groups[1].Value));
            }

            // DataAnnotations attributes can only carry English text; the localized validation
            // component looks the message up as a key, so the attribute text is a key too.
            foreach (Match match in ErrorMessages.Matches(source))
            {
                keys.Add(Unescape(match.Groups[1].Value));
            }

            foreach (Match call in PluralCalls.Matches(source))
            {
                foreach (Match literal in StringLiterals.Matches(call.Value))
                {
                    keys.Add(Unescape(literal.Groups[1].Value));
                }
            }
        }

        return keys;
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = Path.Combine(RepoRoot(), "src", "Server");
        return Directory
            .EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
    }

    // Razor and C# string literals escape the quote and the backslash; the catalogue keys are the
    // unescaped text. No other escape is used in UI copy today, and one that appeared would be
    // caught by the coverage assertion (the escaped form would not match any key).
    private static string Unescape(string literal) =>
        literal.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

    private static string RepoRoot()
    {
        for (var directory = AppContext.BaseDirectory; !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "Zenkins.slnx")))
            {
                return directory;
            }
        }

        Assert.Fail("the repository root (Zenkins.slnx) was not found above the test output.");
        return string.Empty;
    }
}
