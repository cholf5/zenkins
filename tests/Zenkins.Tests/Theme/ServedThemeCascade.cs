using System.Text;

namespace Zenkins.Tests.Theme;

/// <summary>
/// How the cascade model answers for one custom property. Three states, not two: "a declaration I
/// can see wins" has to stay distinguishable from "something might win and I cannot tell", or a
/// fact written as <c>Assert.False(tokens.ContainsKey("--x"))</c> would pass by refusing to answer.
/// </summary>
internal enum CascadeState
{
    /// <summary>One declaration wins under every reading of the cascade this reader models.</summary>
    Declared,

    /// <summary>
    /// A declaration exists, but whether it applies (or how strongly it ranks) depends on a
    /// condition, at-rule or selector this reader does not model. There is no value, on purpose.
    /// </summary>
    Unknown,

    /// <summary>No declaration of the property reaches the modelled document at all.</summary>
    NotDeclared,
}

/// <summary>One property's verdict, with the reason an <see cref="CascadeState.Unknown"/> happened.</summary>
internal readonly record struct CascadeAnswer(CascadeState State, string? Value, string? Reason)
{
    public static readonly CascadeAnswer Absent = new(CascadeState.NotDeclared, null, null);

    public static CascadeAnswer Of(string value) => new(CascadeState.Declared, value, null);

    public static CascadeAnswer Unsure(string reason) => new(CascadeState.Unknown, null, reason);
}

/// <summary>The whole document's cascade verdict: every token name the sheets mention, three-state.</summary>
internal sealed class CascadeResolution
{
    private readonly Dictionary<string, CascadeAnswer> _answers;

    internal CascadeResolution(Dictionary<string, CascadeAnswer> answers)
    {
        _answers = answers;

        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        var unknown = new List<string>();
        foreach (var (name, answer) in answers)
        {
            if (answer.State == CascadeState.Declared && answer.Value is not null)
            {
                tokens[name] = answer.Value;
            }
            else if (answer.State == CascadeState.Unknown)
            {
                unknown.Add(name);
            }
        }

        unknown.Sort(StringComparer.Ordinal);
        Tokens = tokens;
        UnknownTokens = unknown;
        Names = answers.Keys.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The tokens that resolve to one definite value. An <see cref="CascadeState.Unknown"/> token is
    /// absent here on purpose, so a fact that asserts a value fails closed instead of reading a
    /// winner the reader is not entitled to.
    /// </summary>
    public IReadOnlyDictionary<string, string> Tokens { get; }

    /// <summary>The tokens that exist but cannot be answered, sorted, for diagnostics.</summary>
    public IReadOnlyList<string> UnknownTokens { get; }

    /// <summary>
    /// Every token name the sheets mention — <see cref="Tokens"/> plus <see cref="UnknownTokens"/>.
    /// A fact that compares two document states should walk this, not <see cref="Tokens"/>, or a
    /// property that is unknown in both states would be silently skipped.
    /// </summary>
    public IReadOnlyList<string> Names { get; }

    public CascadeAnswer Answer(string name) =>
        _answers.TryGetValue(name, out var answer) ? answer : CascadeAnswer.Absent;
}

/// <summary>
/// The smallest CSS reader the theme shell tests can get away with: it reads the <c>&lt;head&gt;</c>'s
/// stylesheet links in document order, annotates every rule with its layer and its enclosing
/// at-rules, and resolves custom properties the way the cascade would for one fixed document — an
/// <c>&lt;html&gt;</c> carrying an optional <c>class="dark"</c> and the two theme <c>data-*</c>
/// attributes and nothing else that any selector could key off, with no ancestors, no siblings and
/// no shadow tree. Every selector, condition and
/// layer is judged against that document, and anything the reader cannot place is answered
/// <see cref="CascadeState.Unknown"/> instead of being resolved by accident.
/// <para>
/// Why not a real browser: this repository's gate is <c>dotnet test</c> with no Node.js and no
/// browser download (see docs/development.md §1), so a headless-Chromium assertion would be a
/// second toolchain in the developer loop. The price of that choice is that the reader has to know
/// exactly what it is modelling, and answer <see cref="CascadeState.Unknown"/> for everything else
/// — see <see cref="Resolve(IReadOnlyList{string}, bool, string?, string?, bool?)"/>.
/// </para>
/// <para>
/// What it models, and what it therefore answers:
/// <list type="bullet">
/// <item><b>Specificity</b> is the real (ids, classes, types) triple of the subject compound, so
/// <c>html:root</c> is (0,1,1) and <c>[data-base-color="zinc"]</c> is (0,1,0). Counting element
/// selectors as zero made those two tie, and the tie then fell to document order — the palette
/// sheet, which is loaded last, took a fight a browser gives to the element+root compound. No
/// served sheet actually exercises that today (none contains <c>html:root</c>), so it is pinned
/// synthetically in ThemeCascadeModelTests rather than measured on this app.</item>
/// <item><b>Cascade layers</b> are ordered by first appearance across the document, and the
/// unlayered bucket sits where CSS Cascading 5 §6.4 puts it: strongest for normal declarations,
/// weakest for <c>!important</c> ones. This is not optional — <c>blazorblueprint.css</c> declares
/// <c>:root { --sidebar-width: 16rem }</c> inside <c>@layer base</c>, so the unlayered
/// <c>15rem</c> in <c>styles/theme.css</c> wins in a browser. B-007's comment claiming "every
/// sheet that declares these tokens is unlayered" was simply false, and it both hid that value and
/// forbade the reader from answering <c>--sidebar-width-icon</c> correctly (that token is declared
/// <em>only</em> inside <c>@layer base</c>).</item>
/// <item><b><c>!important</c></b> is parsed off the value and ranked, so a colour never keeps a
/// stray <c>!important</c> in it and importance cannot be outranked by specificity.</item>
/// <item><b>Conditions</b>: <c>@media (prefers-color-scheme: dark|light)</c> is answered, but only
/// when the caller states the preference (the gate never does). Every other <c>@media</c> query,
/// and every <c>@supports</c>/<c>@container</c>/unknown at-rule, is <see cref="CascadeState.Unknown"/>
/// for the properties it declares rather than being applied unconditionally.</item>
/// <item><b>Selectors</b>: only the shape that can select this document's <c>&lt;html&gt;</c> is
/// evaluated — type selectors, <c>*</c>, <c>:root</c>, <c>.dark</c>, the two theme attributes,
/// <c>:host</c> and pseudo-elements (which never match <c>&lt;html&gt;</c> itself). Anything else
/// (<c>:is()</c>, <c>:where()</c>, <c>:hover</c>, escapes this parser does not resolve) makes the
/// properties of that rule unknown instead of silently unmatched.</item>
/// </list>
/// </para>
/// <para>
/// What it still does not model, and says so instead of guessing: cascade origins other than the
/// author's (no user-agent or user sheet is in the list), <c>@scope</c>, registered-property
/// (<c>@property</c>) initial values, <c>revert-layer</c>, and anything inside an <c>@supports</c>.
/// </para>
/// </summary>
internal static class ServedThemeCascade
{
    private static readonly string[] OpaqueAtRules =
    [
        // At-rules whose blocks hold no style rules for an element: @keyframes holds keyframe
        // selectors, @property/@font-face/@page/@counter-style declare descriptors. Skipping them
        // is knowledge, not a guess — they cannot declare a custom property on <html>.
        "charset", "import", "namespace", "keyframes", "-webkit-keyframes",
        "font-face", "property", "page", "counter-style", "font-feature-values",
        "font-palette-values", "viewport", "-ms-viewport", "starting-style",
    ];

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
    /// Every rule of one served sheet, flattened: selector list plus declaration block text, with
    /// no cascade metadata. Kept for <see cref="DeclaredValue"/>; the cascade itself uses the
    /// annotated walk in <see cref="Resolve(IReadOnlyList{string}, bool, string?, string?, bool?)"/>.
    /// </summary>
    public static List<(string[] Selectors, string Declarations)> Rules(string css)
    {
        var rules = new List<CascadeRule>();
        var layers = new List<string>();
        Collect(StripComments(css), layer: null, conditions: [], rules, layers);
        return rules.Select(rule => (rule.Selectors, rule.Declarations)).ToList();
    }

    /// <summary>
    /// Resolves the custom properties that win for a document in the given state.
    /// </summary>
    /// <param name="sheetsInHeadOrder">The served stylesheet bodies, in <c>&lt;head&gt;</c> order.</param>
    /// <param name="isDark">Whether <c>&lt;html&gt;</c> carries <c>class="dark"</c>.</param>
    /// <param name="baseColor">The <c>data-base-color</c> attribute, or <see langword="null"/> for none.</param>
    /// <param name="primaryColor">The <c>data-primary-color</c> attribute, or <see langword="null"/> for none.</param>
    /// <param name="prefersDark">
    /// What <c>prefers-color-scheme</c> would report, or <see langword="null"/> when the caller does
    /// not know — in which case a <c>@media (prefers-color-scheme: …)</c> block leaves its tokens
    /// <see cref="CascadeState.Unknown"/> rather than being taken in both modes.
    /// </param>
    public static CascadeResolution Resolve(
        IReadOnlyList<string> sheetsInHeadOrder,
        bool isDark,
        string? baseColor = null,
        string? primaryColor = null,
        bool? prefersDark = null)
    {
        var layers = new List<string>();
        var rules = new List<CascadeRule>();
        foreach (var css in sheetsInHeadOrder)
        {
            Collect(StripComments(css), layer: null, conditions: [], rules, layers);
        }

        var layerIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < layers.Count; index++)
        {
            layerIndexes[layers[index]] = index;
        }

        var document = new DocumentState(isDark, baseColor, primaryColor);
        var winners = new Dictionary<string, (Rank Rank, string Value)>(StringComparer.Ordinal);
        var doubts = new Dictionary<string, Doubt>(StringComparer.Ordinal);

        for (var order = 0; order < rules.Count; order++)
        {
            var rule = rules[order];
            var tokens = TokenDeclarations(rule.Declarations);
            if (tokens.Count == 0)
            {
                continue;
            }

            var (conditions, blocker) = EvaluateConditions(rule.Conditions, prefersDark);
            if (conditions == Verdict.False)
            {
                continue;
            }

            var matched = Specificity.Zero;
            var matches = false;
            string? unreadableSelector = null;
            foreach (var selector in rule.Selectors)
            {
                var verdict = Match(selector, document, out var specificity);
                if (verdict == MatchVerdict.Matches)
                {
                    matches = true;
                    if (specificity.CompareTo(matched) > 0)
                    {
                        matched = specificity;
                    }
                }
                else if (verdict == MatchVerdict.Unknown && unreadableSelector is null)
                {
                    unreadableSelector = selector;
                }
            }

            if (!matches && unreadableSelector is null)
            {
                // No selector of this rule can select <html>: the rule is inert for every property
                // it declares, which is a definite answer, not a gap.
                continue;
            }

            foreach (var (name, value, important) in tokens)
            {
                if (unreadableSelector is not null)
                {
                    RecordDoubt(
                        doubts,
                        name,
                        rank: null,
                        $"the selector '{unreadableSelector}' is not one this reader evaluates");
                    continue;
                }

                var rank = new Rank(
                    important,
                    LayerRank(rule.Layer, layerIndexes, layers.Count, important),
                    matched,
                    order);

                if (conditions == Verdict.Unknown)
                {
                    // The declaration may or may not apply; either way the property has no answer
                    // the resolver is entitled to give, so it refuses instead of taking the block.
                    RecordDoubt(doubts, name, rank, $"'{blocker}' is not a condition this reader evaluates");
                    continue;
                }

                if (!winners.TryGetValue(name, out var current) || rank.CompareTo(current.Rank) > 0)
                {
                    winners[name] = (rank, value);
                }
            }
        }

        var answers = new Dictionary<string, CascadeAnswer>(StringComparer.Ordinal);
        foreach (var (name, winner) in winners)
        {
            answers[name] = doubts.TryGetValue(name, out var doubt) && !doubt.LosesTo(winner.Rank)
                ? CascadeAnswer.Unsure(doubt.Reason)
                : CascadeAnswer.Of(winner.Value);
        }

        foreach (var (name, doubt) in doubts)
        {
            answers.TryAdd(name, CascadeAnswer.Unsure(doubt.Reason));
        }

        return new CascadeResolution(answers);
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

            foreach (var (name, value, _) in Declarations(declarations))
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
    /// is not empty, not <c>transparent</c>/<c>none</c>, not an unresolved <c>var()</c>, not a value
    /// that still carries an unparsed <c>!</c>, and not a color whose alpha channel is zero.
    /// Unknown syntax answers <see langword="false"/> on purpose.
    /// </summary>
    public static bool IsOpaqueColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var color = value.Trim();
        if (color.Contains("var(", StringComparison.Ordinal)
            || color.Contains('!')
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

    // ── the cascade ─────────────────────────────────────────────────────────────────────────────

    private enum Verdict
    {
        True,
        False,
        Unknown,
    }

    private enum MatchVerdict
    {
        Matches,
        DoesNotMatch,
        Unknown,
    }

    private readonly record struct DocumentState(bool IsDark, string? BaseColor, string? PrimaryColor);

    /// <summary>One annotated rule: selector list, body, the layer it sits in, the conditions around it.</summary>
    private sealed record CascadeRule(
        string[] Selectors,
        string Declarations,
        string? Layer,
        string[] Conditions);

    private readonly record struct Specificity(int Ids, int Classes, int Types) : IComparable<Specificity>
    {
        public static readonly Specificity Zero = default;

        public int CompareTo(Specificity other)
        {
            var ids = Ids.CompareTo(other.Ids);
            if (ids != 0)
            {
                return ids;
            }

            var classes = Classes.CompareTo(other.Classes);
            return classes != 0 ? classes : Types.CompareTo(other.Types);
        }
    }

    /// <summary>
    /// The cascade order for one declaration, low to high: importance, then layer position
    /// (reversed for important declarations, per CSS Cascading 5 §6.4), then specificity, then
    /// document order.
    /// </summary>
    private readonly record struct Rank(bool Important, int Layer, Specificity Specificity, int Order)
        : IComparable<Rank>
    {
        public int CompareTo(Rank other)
        {
            var important = Important.CompareTo(other.Important);
            if (important != 0)
            {
                return important;
            }

            var layer = Layer.CompareTo(other.Layer);
            if (layer != 0)
            {
                return layer;
            }

            var specificity = Specificity.CompareTo(other.Specificity);
            return specificity != 0 ? specificity : Order.CompareTo(other.Order);
        }
    }

    /// <summary>
    /// A declaration this reader cannot place. A <see langword="null"/> rank means even its
    /// specificity is unknown, so it can never be dismissed by a definite winner.
    /// </summary>
    private readonly record struct Doubt(Rank? Rank, string Reason)
    {
        /// <summary>Whether a definite winner of this rank is provably stronger than this doubt.</summary>
        public bool LosesTo(Rank winner) => Rank is { } rank && rank.CompareTo(winner) < 0;
    }

    private static void RecordDoubt(
        Dictionary<string, Doubt> doubts,
        string name,
        Rank? rank,
        string reason)
    {
        if (!doubts.TryGetValue(name, out var current))
        {
            doubts[name] = new Doubt(rank, reason);
            return;
        }

        if (current.Rank is null)
        {
            return;
        }

        if (rank is null || rank.Value.CompareTo(current.Rank.Value) > 0)
        {
            doubts[name] = new Doubt(rank, reason);
        }
    }

    /// <summary>
    /// Where a declaration sits among the document's layers. Normal declarations climb with the
    /// layer order and the unlayered bucket is the strongest; important declarations are exactly
    /// reversed, which puts the unlayered bucket at the bottom of the important ones.
    /// </summary>
    private static int LayerRank(string? layer, Dictionary<string, int> indexes, int count, bool important)
    {
        var index = layer is null ? -1 : indexes[layer];
        if (important)
        {
            return index < 0 ? -1 : count - 1 - index;
        }

        return index < 0 ? count : index;
    }

    /// <summary>
    /// The verdict of the conditions wrapping a rule, with the prelude responsible for a
    /// <see cref="Verdict.Unknown"/> so the caller can say why it refused to answer.
    /// </summary>
    private static (Verdict Verdict, string? Blocker) EvaluateConditions(string[] conditions, bool? prefersDark)
    {
        string? unknown = null;
        foreach (var condition in conditions)
        {
            var verdict = EvaluateCondition(condition, prefersDark);
            if (verdict == Verdict.False)
            {
                return (Verdict.False, condition);
            }

            if (verdict == Verdict.Unknown && unknown is null)
            {
                unknown = condition;
            }
        }

        return unknown is null ? (Verdict.True, null) : (Verdict.Unknown, unknown);
    }

    private static Verdict EvaluateCondition(string condition, bool? prefersDark)
    {
        if (!string.Equals(AtRuleName(condition), "media", StringComparison.OrdinalIgnoreCase))
        {
            // @supports, @container, @scope, …: no feature model here, so nothing inside is asserted.
            return Verdict.Unknown;
        }

        var query = string.Concat(condition["@media".Length..].Trim().Where(character => !char.IsWhiteSpace(character)));
        var asksForDark = query.Equals("(prefers-color-scheme:dark)", StringComparison.OrdinalIgnoreCase);
        var asksForLight = query.Equals("(prefers-color-scheme:light)", StringComparison.OrdinalIgnoreCase);
        if ((!asksForDark && !asksForLight) || prefersDark is not { } prefers)
        {
            return Verdict.Unknown;
        }

        return (asksForDark ? prefers : !prefers) ? Verdict.True : Verdict.False;
    }

    // ── selectors ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether a selector selects the modelled <c>&lt;html&gt;</c>, and with what specificity.
    /// The subject compound is decided first: if it cannot be <c>&lt;html&gt;</c>, the whole
    /// selector cannot match, and the link that does not model a part of it never matters.
    /// </summary>
    private static MatchVerdict Match(string selector, DocumentState document, out Specificity specificity)
    {
        specificity = Specificity.Zero;
        var compounds = SplitCompounds(selector.Trim());
        if (compounds is null || compounds.Count == 0)
        {
            return MatchVerdict.Unknown;
        }

        var verdict = MatchCompound(compounds[^1], document, out specificity);
        if (verdict != MatchVerdict.Matches)
        {
            return verdict;
        }

        // <html> is the root element: it has no ancestors and no siblings, so a descendant, child or
        // sibling combinator can never reach it. This is a definite "no", not a gap.
        return compounds.Count > 1 ? MatchVerdict.DoesNotMatch : MatchVerdict.Matches;
    }

    private static MatchVerdict MatchCompound(string compound, DocumentState document, out Specificity specificity)
    {
        var ids = 0;
        var classes = 0;
        var types = 0;
        var impossible = false;
        var uncertain = false;
        var index = 0;

        while (index < compound.Length)
        {
            var character = compound[index];

            if (character == '*')
            {
                index++;
                continue;
            }

            if (character == '.')
            {
                index++;
                var name = ReadIdentifier(compound, ref index);
                if (name.Length == 0)
                {
                    uncertain = true;
                    continue;
                }

                classes++;
                if (!string.Equals(name, "dark", StringComparison.Ordinal) || !document.IsDark)
                {
                    // The modelled <html> carries no class other than an optional `dark`.
                    impossible = true;
                }

                continue;
            }

            if (character == '#')
            {
                index++;
                var name = ReadIdentifier(compound, ref index);
                if (name.Length == 0)
                {
                    uncertain = true;
                    continue;
                }

                ids++;
                impossible = true; // the modelled <html> carries no id
                continue;
            }

            if (character == '[')
            {
                var close = Matching(compound, index, '[', ']');
                if (close < 0)
                {
                    uncertain = true;
                    index = compound.Length;
                    continue;
                }

                var verdict = MatchAttribute(compound[(index + 1)..close], document);
                index = close + 1;
                classes++;
                if (verdict == MatchVerdict.DoesNotMatch)
                {
                    impossible = true;
                }
                else if (verdict == MatchVerdict.Unknown)
                {
                    uncertain = true;
                }

                continue;
            }

            if (character == ':')
            {
                var pseudoElement = index + 1 < compound.Length && compound[index + 1] == ':';
                index += pseudoElement ? 2 : 1;
                var name = ReadIdentifier(compound, ref index);

                if (index < compound.Length && compound[index] == '(')
                {
                    // :is() / :where() / :not() / :nth-child() / :has(): both the specificity and
                    // the set of elements they select are outside this reader.
                    var close = Matching(compound, index, '(', ')');
                    uncertain = true;
                    index = close < 0 ? compound.Length : close + 1;
                    continue;
                }

                if (name.Length == 0)
                {
                    uncertain = true;
                    continue;
                }

                if (pseudoElement || name is "before" or "after" or "first-line" or "first-letter")
                {
                    // The subject would be a pseudo-element, not <html>'s own computed style.
                    types++;
                    impossible = true;
                    continue;
                }

                switch (name)
                {
                    case "root":
                        classes++;
                        break;
                    case "host":
                        classes++;
                        impossible = true; // no shadow tree in the modelled document
                        break;
                    default:
                        // :hover, :focus, :active, :disabled …: the query is about an interaction
                        // state the model does not fix, so this rule cannot be placed.
                        uncertain = true;
                        break;
                }

                continue;
            }

            if (IsTypeStart(character))
            {
                var name = ReadIdentifier(compound, ref index);
                if (name.Length == 0)
                {
                    uncertain = true;
                    continue;
                }

                types++;
                if (!string.Equals(name, "html", StringComparison.OrdinalIgnoreCase))
                {
                    // `body`, `aside`, `svg`: the subject of these rules is not <html>.
                    impossible = true;
                }

                continue;
            }

            // An escaped name we do not resolve, a namespace separator, a stray combinator.
            uncertain = true;
            index++;
        }

        specificity = new Specificity(ids, classes, types);
        if (impossible)
        {
            return MatchVerdict.DoesNotMatch;
        }

        return uncertain ? MatchVerdict.Unknown : MatchVerdict.Matches;
    }

    private static MatchVerdict MatchAttribute(string text, DocumentState document)
    {
        var comparison = text.IndexOfAny(['=', '~', '|', '^', '$', '*']);
        var name = (comparison < 0 ? text : text[..comparison]).Trim();
        if (name.Length == 0)
        {
            return MatchVerdict.Unknown;
        }

        string? expected;
        if (string.Equals(name, "data-base-color", StringComparison.OrdinalIgnoreCase))
        {
            expected = document.BaseColor;
        }
        else if (string.Equals(name, "data-primary-color", StringComparison.OrdinalIgnoreCase))
        {
            expected = document.PrimaryColor;
        }
        else
        {
            // The modelled <html> carries exactly the language, the optional `dark` class and these
            // two attributes, so any other attribute selector cannot match it.
            return MatchVerdict.DoesNotMatch;
        }

        if (comparison < 0)
        {
            return expected is null ? MatchVerdict.DoesNotMatch : MatchVerdict.Matches;
        }

        if (text[comparison] != '=')
        {
            return MatchVerdict.Unknown;
        }

        var value = text[(comparison + 1)..].Trim().Trim('"', '\'');
        return expected is not null && string.Equals(value, expected, StringComparison.Ordinal)
            ? MatchVerdict.Matches
            : MatchVerdict.DoesNotMatch;
    }

    /// <summary>
    /// Splits a selector into compounds, or a prelude into its selector list, at top-level
    /// separators — a comma inside <c>:is(a, b)</c> and a space inside <c>[title="a b"]</c> are not
    /// separators. Returns <see langword="null"/> for anything unbalanced, which the caller answers
    /// as unknown rather than as "no match".
    /// </summary>
    private static List<string>? SplitTopLevel(string text, Func<char, bool> isSeparator)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var brackets = 0;
        var parentheses = 0;
        char? quote = null;

        void Flush()
        {
            var part = current.ToString().Trim();
            current.Clear();
            if (part.Length > 0)
            {
                parts.Add(part);
            }
        }

        foreach (var character in text)
        {
            if (quote is not null)
            {
                current.Append(character);
                if (character == quote)
                {
                    quote = null;
                }

                continue;
            }

            switch (character)
            {
                case '"' or '\'':
                    quote = character;
                    current.Append(character);
                    continue;
                case '[':
                    brackets++;
                    current.Append(character);
                    continue;
                case ']':
                    if (--brackets < 0)
                    {
                        return null;
                    }

                    current.Append(character);
                    continue;
                case '(':
                    parentheses++;
                    current.Append(character);
                    continue;
                case ')':
                    if (--parentheses < 0)
                    {
                        return null;
                    }

                    current.Append(character);
                    continue;
            }

            if (brackets > 0 || parentheses > 0 || !isSeparator(character))
            {
                current.Append(character);
                continue;
            }

            Flush();
        }

        if (quote is not null || brackets != 0 || parentheses != 0)
        {
            return null;
        }

        Flush();
        return parts;
    }

    private static List<string>? SplitCompounds(string selector) =>
        SplitTopLevel(selector, character => character is ' ' or '\t' or '\r' or '\n' or '>' or '+' or '~');

    /// <summary>The selector list of one rule's prelude; an unbalanced prelude keeps its text intact.</summary>
    private static string[] SplitSelectorList(string prelude) =>
        (SplitTopLevel(prelude, character => character == ',') ?? [prelude]).ToArray();

    private static string ReadIdentifier(string text, ref int index)
    {
        var builder = new StringBuilder();
        while (index < text.Length)
        {
            var character = text[index];
            if (character == '\\' && index + 1 < text.Length)
            {
                builder.Append(text[index + 1]);
                index += 2;
                continue;
            }

            if (char.IsLetterOrDigit(character) || character is '-' or '_' || character > 127)
            {
                builder.Append(character);
                index++;
                continue;
            }

            break;
        }

        return builder.ToString();
    }

    private static bool IsTypeStart(char character) =>
        char.IsLetter(character) || character is '-' or '_' || character > 127;

    private static int Matching(string text, int open, char opener, char closer)
    {
        var depth = 0;
        char? quote = null;
        for (var index = open; index < text.Length; index++)
        {
            var character = text[index];
            if (quote is not null)
            {
                if (character == quote)
                {
                    quote = null;
                }

                continue;
            }

            if (character == '"' || character == '\'')
            {
                quote = character;
            }
            else if (character == opener)
            {
                depth++;
            }
            else if (character == closer)
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    // ── sheets ──────────────────────────────────────────────────────────────────────────────────

    private static void Collect(
        string css,
        string? layer,
        string[] conditions,
        List<CascadeRule> rules,
        List<string> layers)
    {
        var index = 0;
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
                var text = css[index..statement].Trim();
                if (string.Equals(AtRuleName(text), "layer", StringComparison.OrdinalIgnoreCase))
                {
                    // `@layer a, b;`: naming layers without a block is what fixes their order before
                    // any of their blocks appear, so it is part of the model, not noise.
                    foreach (var declared in text["@layer".Length..].Split(','))
                    {
                        Register(LayerPath(layer, declared.Trim()), layers);
                    }
                }

                index = statement + 1;
                continue;
            }

            var close = MatchingBrace(css, open);
            var prelude = css[index..open].Trim();
            var body = css[(open + 1)..close];
            index = close + 1;

            if (!prelude.StartsWith('@'))
            {
                if (prelude.Length > 0)
                {
                    rules.Add(new CascadeRule(SplitSelectorList(prelude), body, layer, conditions));
                }

                continue;
            }

            var name = AtRuleName(prelude);
            if (OpaqueAtRules.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(name, "layer", StringComparison.OrdinalIgnoreCase))
            {
                var path = LayerPath(layer, prelude["@layer".Length..].Trim());
                Register(path, layers);
                Collect(body, path, conditions, rules, layers);
                continue;
            }

            // @media, @supports, @container, @scope and anything else with a block: descend, and
            // let the condition be judged when a declaration inside it is actually needed.
            Collect(body, layer, [.. conditions, prelude], rules, layers);
        }
    }

    private static string LayerPath(string? parent, string name)
    {
        if (name.Length == 0)
        {
            // An anonymous layer still orders; give it a stable unique name.
            name = "<anonymous>";
        }

        return parent is null ? name : $"{parent}.{name}";
    }

    private static void Register(string path, List<string> layers)
    {
        if (!layers.Contains(path, StringComparer.Ordinal))
        {
            layers.Add(path);
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

    private static string AtRuleName(string prelude)
    {
        var text = prelude.TrimStart();
        if (!text.StartsWith('@'))
        {
            return string.Empty;
        }

        var end = 1;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '-' or '_'))
        {
            end++;
        }

        return text[1..end];
    }

    // ── declarations ────────────────────────────────────────────────────────────────────────────

    private static List<(string Name, string Value, bool Important)> TokenDeclarations(string declarations) =>
        Declarations(declarations)
            .Where(declaration => declaration.Name.StartsWith("--", StringComparison.Ordinal))
            .ToList();

    private static List<(string Name, string Value, bool Important)> Declarations(string block)
    {
        var declarations = new List<(string Name, string Value, bool Important)>();
        foreach (var piece in SplitDeclarations(block))
        {
            var colon = piece.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = piece[..colon].Trim();
            if (name.Length == 0 || name.Contains('{') || name.Contains('}') || name.Contains('"'))
            {
                continue;
            }

            var (value, important) = SplitImportance(piece[(colon + 1)..].Trim());
            declarations.Add((name, value, important));
        }

        return declarations;
    }

    /// <summary>
    /// Splits a declaration block on its top-level semicolons, so a semicolon inside a function or a
    /// quoted string (<c>url("data:…;charset=…")</c>) cannot cut a declaration in half.
    /// </summary>
    private static IEnumerable<string> SplitDeclarations(string block)
    {
        var parentheses = 0;
        char? quote = null;
        var start = 0;

        for (var index = 0; index < block.Length; index++)
        {
            var character = block[index];
            if (quote is not null)
            {
                if (character == quote)
                {
                    quote = null;
                }

                continue;
            }

            switch (character)
            {
                case '"' or '\'':
                    quote = character;
                    break;
                case '(':
                    parentheses++;
                    break;
                case ')':
                    if (parentheses > 0)
                    {
                        parentheses--;
                    }

                    break;
                case ';' when parentheses == 0:
                    yield return block[start..index];
                    start = index + 1;
                    break;
            }
        }

        yield return block[start..];
    }

    private static (string Value, bool Important) SplitImportance(string value)
    {
        var bang = value.LastIndexOf('!');
        if (bang < 0)
        {
            return (value, false);
        }

        var flag = string.Concat(value[(bang + 1)..].Where(character => !char.IsWhiteSpace(character)));
        if (!flag.Equals("important", StringComparison.OrdinalIgnoreCase))
        {
            return (value, false);
        }

        return (value[..bang].TrimEnd(), true);
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
