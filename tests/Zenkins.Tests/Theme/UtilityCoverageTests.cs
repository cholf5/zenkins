using System.Text.RegularExpressions;
using Xunit;

namespace Zenkins.Tests.Theme;

/// <summary>
/// The app's `bb:` utility classes are Tailwind classes in a pre-built, purged sheet, so a class the
/// package does not ship is silently inert — no build error, no browser warning, a diff that reads
/// fine. That is how a submit button sat 24px above its input (`bb:sm:items-end`) and how eight
/// "full row" grid cells stayed one column wide (`bb:sm:col-span-2/3`); both were only found by
/// measuring a real render.
/// <para>
/// These facts turn the whole class of bug into a build failure, the way F-043 does for localization
/// keys: the left side is every `bb:` class a `.razor` file mentions, the right side is every class
/// name in a stylesheet the app actually serves — nothing is pinned by hand, so the check keeps
/// working as the markup and the package both move.
/// </para>
/// </summary>
[Trait("Category", "Theme")]
public sealed class UtilityCoverageTests
{
    private const string ShimPath = "styles/utilities.css";

    /// <summary>A `bb:` utility token in markup. Stops at the characters that end a class.</summary>
    private static readonly Regex UtilityInMarkup = new(
        "bb:[^\\s\"'<>{}(),;`]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A class name in a stylesheet, with the CSS escapes removed (`bg-input\/30` → `bg-input/30`).</summary>
    private static readonly Regex ClassInStylesheet = new(
        "\\.((?:[A-Za-z0-9_-]|\\\\.)+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact(Timeout = 3 * 60 * 1000)]
    public async Task Every_bb_utility_the_markup_uses_is_defined_by_a_served_stylesheet()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        var sheets = await ShellStylesheets.LoadAsync();
        var defined = DefinedClasses(sheets.Bodies);

        var used = UsedUtilities();
        Assert.NotEmpty(used);

        var missing = used.Where(pair => !defined.Contains(pair.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        Assert.True(
            missing.Length == 0,
            "These `bb:` classes are not defined by any stylesheet the app serves, so they do nothing "
            + "and the browser cannot warn about them. Define them in styles/utilities.css (with the "
            + "package's own scale) or use a class the package ships:\n"
            + string.Join(
                "\n",
                missing.Select(pair => $"  {pair.Key}  <- {string.Join(", ", pair.Value.Order(StringComparer.Ordinal))}")));
    }

    [Fact(Timeout = 3 * 60 * 1000)]
    public async Task The_utility_shim_defines_nothing_the_package_already_ships()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        var sheets = await ShellStylesheets.LoadAsync();

        // A package upgrade that ships one of these would leave two definitions of the same utility
        // in the cascade, and ours would silently win or lose by document order.
        var package = DefinedClasses([sheets.ByHref[ShellStylesheets.FrameworkPath]]);
        var shim = DefinedClasses([sheets.ByHref[ShimPath]]);

        var duplicates = shim.Intersect(package, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            duplicates.Length == 0,
            $"styles/utilities.css redefines {string.Join(", ", duplicates)}, which "
            + "blazorblueprint.css already ships. Delete the shim's copy.");
    }

    [Fact(Timeout = 3 * 60 * 1000)]
    public async Task The_utility_shim_carries_no_rule_the_markup_does_not_need()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        var sheets = await ShellStylesheets.LoadAsync();
        var used = UsedUtilities();

        var unused = DefinedClasses([sheets.ByHref[ShimPath]])
            .Where(name => name.StartsWith("bb:", StringComparison.Ordinal) && !used.ContainsKey(name))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            unused.Length == 0,
            $"styles/utilities.css defines {string.Join(", ", unused)}, which no .razor file uses. "
            + "The file is a workaround list, not a utility library: drop the rule.");
    }

    /// <summary>Every <c>bb:</c> class a component mentions, mapped to the files that mention it.</summary>
    private static Dictionary<string, HashSet<string>> UsedUtilities()
    {
        var root = Path.Combine(FindRepositoryRoot(), "src", "Server", "Components");
        var used = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in UtilityInMarkup.Matches(text))
            {
                var token = match.Value;

                // A class assembled at run time cannot be read from source; the guard covers the
                // literal ones, which is where an inert class hides.
                if (token.Contains('@', StringComparison.Ordinal))
                {
                    continue;
                }

                if (!used.TryGetValue(token, out var files))
                {
                    used[token] = files = new HashSet<string>(StringComparer.Ordinal);
                }

                files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
        }

        return used;
    }

    private static HashSet<string> DefinedClasses(IEnumerable<string> sheets)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var css in sheets)
        {
            foreach (Match match in ClassInStylesheet.Matches(css))
            {
                names.Add(Regex.Unescape(match.Groups[1].Value));
            }
        }

        return names;
    }

    /// <summary>Walks up from the test binary until the directory that holds the solution file.</summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Zenkins.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException($"Zenkins.slnx was not found above {AppContext.BaseDirectory}.");
    }
}
