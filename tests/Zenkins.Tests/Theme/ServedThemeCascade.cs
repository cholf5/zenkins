using System.Text;

namespace Zenkins.Tests.Theme;

/// <summary>
/// The smallest CSS reader the theme shell tests can get away with: it reads the <c>&lt;head&gt;</c>'s
/// stylesheet links in document order, flattens each served sheet into rules (descending into
/// at-rules, because the package hides utilities inside <c>@layer</c>/<c>@media</c>/<c>@supports</c>),
/// and resolves custom properties the way the cascade would for a document whose <c>&lt;html&gt;</c>
/// carries no <c>data-*</c> theme attributes.
/// <para>
/// Why not a real browser: this repository's gate is <c>dotnet test</c> with no Node.js and no
/// browser download (see docs/development.md §1), so a headless-Chromium assertion would be a
/// second toolchain in the developer loop. What is modelled here is exactly what B-007 got wrong,
/// and it fails closed: an unmodelled selector or an unrecognised color syntax answers "no match"
/// or "not a color" rather than passing. The one cascade rule it does not model is layering — and
/// it does not have to, because every sheet that declares these tokens is unlayered, so document
/// order plus selector specificity decides, which is what <see cref="Tokens"/> implements.
/// </para>
/// </summary>
internal static class ServedThemeCascade
{
    /// <summary>The stylesheet <c>href</c>s a page references, in the order its head lists them.</summary>
    public static List<string> StylesheetHrefs(string html)
    {
        var hrefs = new List<string>();
        var index = 0;
        while (true)
        {
            var link = html.IndexOf("<link", index, StringComparison.Ordinal);
            if (link < 0)
            {
                return hrefs;
            }

            var end = html.IndexOf('>', link);
            if (end < 0)
            {
                return hrefs;
            }

            var tag = html[link..end];
            index = end + 1;

            if (!tag.Contains("rel=\"stylesheet\"", StringComparison.Ordinal))
            {
                continue;
            }

            var href = Attribute(tag, "href");
            if (href is not null)
            {
                hrefs.Add(href);
            }
        }
    }

    /// <summary>
    /// Every rule of one served sheet, flattened: selector list plus declaration block text.
    /// </summary>
    public static List<(string[] Selectors, string Declarations)> Rules(string css)
    {
        var rules = new List<(string[] Selectors, string Declarations)>();
        Collect(StripComments(css), 0, rules);
        return rules;
    }

    /// <summary>
    /// The custom properties that win for a document in the given state, resolved only as far as
    /// which declaration wins. Higher specificity wins; on a tie the later rule in the later sheet
    /// wins — the browser's own rule for these selectors.
    /// </summary>
    /// <param name="sheetsInHeadOrder">The served stylesheet bodies, in <c>&lt;head&gt;</c> order.</param>
    /// <param name="isDark">Whether <c>&lt;html&gt;</c> carries <c>class="dark"</c>.</param>
    /// <param name="baseColor">The <c>data-base-color</c> attribute, or <see langword="null"/> for none.</param>
    /// <param name="primaryColor">The <c>data-primary-color</c> attribute, or <see langword="null"/> for none.</param>
    public static Dictionary<string, string> Tokens(
        IReadOnlyList<string> sheetsInHeadOrder,
        bool isDark,
        string? baseColor = null,
        string? primaryColor = null)
    {
        var winners = new Dictionary<string, (int Specificity, int Order, string Value)>(StringComparer.Ordinal);
        var order = 0;

        foreach (var css in sheetsInHeadOrder)
        {
            foreach (var (selectors, declarations) in Rules(css))
            {
                order++;
                var properties = CustomProperties(declarations);
                if (properties.Count == 0)
                {
                    continue;
                }

                foreach (var selector in selectors)
                {
                    if (!Matches(selector, isDark, baseColor, primaryColor, out var specificity))
                    {
                        continue;
                    }

                    foreach (var (name, value) in properties)
                    {
                        if (!winners.TryGetValue(name, out var current)
                            || specificity > current.Specificity
                            || (specificity == current.Specificity && order >= current.Order))
                        {
                            winners[name] = (specificity, order, value);
                        }
                    }
                }
            }
        }

        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, winner) in winners)
        {
            tokens[name] = winner.Value;
        }

        return tokens;
    }

    /// <summary>The value a plain rule declares for one property, or <see langword="null"/>.</summary>
    public static string? DeclaredValue(string css, string selector, string property)
    {
        foreach (var (selectors, declarations) in Rules(css))
        {
            if (!selectors.Contains(selector, StringComparer.Ordinal))
            {
                continue;
            }

            foreach (var (name, value) in Declarations(declarations))
            {
                if (string.Equals(name, property, StringComparison.Ordinal))
                {
                    return value;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Substitutes <c>var(--token)</c> (and <c>var(--token, fallback)</c>) from a resolved token map.
    /// An undefined token with no fallback answers <see langword="null"/>: the browser would drop
    /// that declaration, which is the failure mode this whole test exists to catch.
    /// </summary>
    public static string? Resolve(string? value, IReadOnlyDictionary<string, string> tokens)
    {
        if (value is null)
        {
            return null;
        }

        var resolved = value.Trim();
        for (var guard = 0; guard < 8; guard++)
        {
            var start = resolved.IndexOf("var(", StringComparison.Ordinal);
            if (start < 0)
            {
                return resolved;
            }

            var close = resolved.IndexOf(')', start);
            if (close < 0)
            {
                return null;
            }

            var arguments = resolved[(start + 4)..close];
            var comma = arguments.IndexOf(',');
            var name = (comma < 0 ? arguments : arguments[..comma]).Trim();
            string replacement;
            if (tokens.TryGetValue(name, out var token))
            {
                replacement = token;
            }
            else if (comma >= 0)
            {
                replacement = arguments[(comma + 1)..].Trim();
            }
            else
            {
                return null;
            }

            resolved = string.Concat(resolved.AsSpan(0, start), replacement, resolved.AsSpan(close + 1));
        }

        return null;
    }

    /// <summary>
    /// Whether a CSS color value is a real color the browser would paint something with — i.e. it
    /// is not empty, not <c>transparent</c>/<c>none</c>, not an unresolved <c>var()</c>, and not a
    /// color whose alpha channel is zero. Unknown syntax answers <see langword="false"/> on purpose.
    /// </summary>
    public static bool IsOpaqueColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var color = value.Trim();
        if (color.Contains("var(", StringComparison.Ordinal)
            || color.Equals("transparent", StringComparison.OrdinalIgnoreCase)
            || color.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] functions =
        [
            "oklch(", "oklab(", "lab(", "lch(", "rgb(", "rgba(", "hsl(", "hsla(", "color(",
        ];

        foreach (var function in functions)
        {
            if (!color.StartsWith(function, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // `oklch(l c h / a)` and friends: a slash introduces alpha.
            var slash = color.IndexOf('/');
            if (slash >= 0)
            {
                return !IsZeroNumber(color[(slash + 1)..].TrimEnd(')'));
            }

            // `rgba(r, g, b, a)` / `hsla(h, s, l, a)`: the fourth comma-separated argument.
            if (function is "rgba(" or "hsla(")
            {
                var arguments = color[function.Length..].TrimEnd(')').Split(',');
                return arguments.Length < 4 || !IsZeroNumber(arguments[3]);
            }

            return true;
        }

        if (!color.StartsWith('#'))
        {
            return false;
        }

        return color.Length switch
        {
            // #rgb / #rrggbb are always opaque; #rgba / #rrggbbaa are transparent at alpha 0.
            3 or 6 => true,
            4 => color[3] != '0',
            8 => !color[6..].Equals("00", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool IsZeroNumber(string text) =>
        double.TryParse(
            text.Trim().TrimEnd('%'),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var number) && number == 0;

    /// <summary>
    /// Whether a selector matches the modelled document. Deliberately narrow: the selectors that
    /// declare theme tokens are <c>:root</c>, <c>.dark</c>, their <c>html</c>-qualified twins, and
    /// the two data-attribute compounds; anything else answers <see langword="false"/>.
    /// </summary>
    private static bool Matches(
        string selector,
        bool isDark,
        string? baseColor,
        string? primaryColor,
        out int specificity)
    {
        specificity = 0;
        var text = selector.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var rest = RemoveAttribute(ref text, "data-base-color", baseColor);
        if (rest is false)
        {
            return false;
        }

        if (RemoveAttribute(ref text, "data-primary-color", primaryColor) is false)
        {
            return false;
        }

        var needsDark = text.Contains(".dark", StringComparison.Ordinal);
        var remainder = text
            .Replace(".dark", string.Empty, StringComparison.Ordinal)
            .Replace(":root", string.Empty, StringComparison.Ordinal)
            .Replace("html", string.Empty, StringComparison.Ordinal)
            .Replace("body", string.Empty, StringComparison.Ordinal)
            .Replace("*", string.Empty, StringComparison.Ordinal)
            .Trim();

        // A selector shape this probe does not model (a pseudo-class, a descendant combinator, a
        // leftover attribute) never matches: the palette sheets use none of them for token blocks.
        if (remainder.Length != 0 || remainder.Contains(':') || remainder.Contains('['))
        {
            return false;
        }

        specificity = text.Count(character => character == '.')
            + (selector.Contains(":root", StringComparison.Ordinal) ? 1 : 0)
            + selector.Count(character => character == '[');

        return !needsDark || isDark;
    }

    /// <summary>
    /// Checks one <c>[data-x="value"]</c> compound against the modelled document value (absent when
    /// <paramref name="expected"/> is <see langword="null"/>) and strips it from the selector.
    /// </summary>
    private static bool? RemoveAttribute(ref string selector, string name, string? expected)
    {
        var marker = $"[{name}=\"";
        var start = selector.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return true;
        }

        var valueStart = start + marker.Length;
        var valueEnd = selector.IndexOf('"', valueStart);
        if (valueEnd < 0)
        {
            return null;
        }

        var value = selector[valueStart..valueEnd];
        if (!string.Equals(value, expected, StringComparison.Ordinal))
        {
            return false;
        }

        selector = selector.Remove(start, valueEnd - start + 2);
        return true;
    }

    private static void Collect(string css, int start, List<(string[] Selectors, string Declarations)> rules)
    {
        var index = start;
        while (index < css.Length)
        {
            var open = css.IndexOf('{', index);
            if (open < 0)
            {
                return;
            }

            var statement = css.IndexOf(';', index);
            if (statement >= 0 && statement < open)
            {
                // A statement at-rule such as `@layer utilities;` — no block to collect.
                index = statement + 1;
                continue;
            }

            var close = MatchingBrace(css, open);
            var prelude = css[index..open].Trim();
            if (prelude.StartsWith('@'))
            {
                // Descend: `@media` / `@supports` / `@layer` blocks hold ordinary rules.
                Collect(css[(open + 1)..close], 0, rules);
            }
            else if (prelude.Length > 0)
            {
                rules.Add((
                    prelude.Split(',')
                        .Select(selector => selector.Trim())
                        .Where(selector => selector.Length > 0)
                        .ToArray(),
                    css[(open + 1)..close]));
            }

            index = close + 1;
        }
    }

    private static int MatchingBrace(string css, int open)
    {
        var depth = 0;
        for (var index = open; index < css.Length; index++)
        {
            if (css[index] == '{')
            {
                depth++;
            }
            else if (css[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return css.Length;
    }

    private static List<(string Name, string Value)> CustomProperties(string declarations) =>
        Declarations(declarations)
            .Where(declaration => declaration.Name.StartsWith("--", StringComparison.Ordinal))
            .ToList();

    private static List<(string Name, string Value)> Declarations(string block)
    {
        var declarations = new List<(string Name, string Value)>();
        foreach (var piece in block.Split(';'))
        {
            var colon = piece.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = piece[..colon].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            declarations.Add((name, piece[(colon + 1)..].Trim()));
        }

        return declarations;
    }

    private static string StripComments(string css)
    {
        var builder = new StringBuilder(css.Length);
        var index = 0;
        while (index < css.Length)
        {
            var comment = css.IndexOf("/*", index, StringComparison.Ordinal);
            if (comment < 0)
            {
                builder.Append(css, index, css.Length - index);
                return builder.ToString();
            }

            builder.Append(css, index, comment - index);
            var end = css.IndexOf("*/", comment + 2, StringComparison.Ordinal);
            index = end < 0 ? css.Length : end + 2;
        }

        return builder.ToString();
    }

    private static string? Attribute(string tag, string name)
    {
        var marker = $"{name}=\"";
        var start = tag.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var valueStart = start + marker.Length;
        var valueEnd = tag.IndexOf('"', valueStart);
        return valueEnd < 0 ? null : tag[valueStart..valueEnd];
    }
}
