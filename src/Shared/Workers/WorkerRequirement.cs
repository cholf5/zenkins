using System.Text;

namespace Zenkins.Shared.Workers;

/// <summary>
/// What a step (and, once unioned, a run) needs from the Worker that executes it: a boolean expression over
/// label atoms - <c>linux</c>, <c>windows</c>, <c>arm64</c> - written as a comma list
/// (<c>"linux,arm64"</c>, all must match), a table (<c>{ "linux", "arm64" }</c>), or an expression
/// (<c>"linux &amp;&amp; (arm64 || x64)"</c>, <c>"!windows"</c>).
/// <para>
/// <b>Stored as DNF, shown as written.</b> The expression is normalized to disjunctive normal form - a list
/// of clauses, each a set of atoms that must be present or absent - so that every question later asked of it
/// is a set operation: "does this Worker satisfy it", "do these two requirements contradict each other",
/// "what is the union". Normalizing at save time also means a later grammar change cannot reinterpret a run
/// that already exists. The original text is kept for people (<see cref="Expression"/>,
/// <see cref="ReadableForm"/>); the DNF is what is stored and compared (<see cref="StorageForm"/>).
/// </para>
/// <para>
/// <b>Bounded.</b> <c>!a &amp;&amp; !b &amp;&amp; ...</c> is a thousand tags written once, and a cross product of two
/// alternatives explodes - so a requirement with more than <see cref="MaxDisjuncts"/> alternatives is
/// refused with <see cref="TooComplex"/> rather than stored.
/// </para>
/// <para>
/// <b>No requirement is not an empty expression.</b> <see cref="None"/> is the tautology (one empty clause):
/// any Worker satisfies it, including one with no tags. An expression whose clauses all cancel
/// (<c>linux &amp;&amp; !linux</c>) is the opposite - unsatisfiable, <see cref="IsContradiction"/> true - and is
/// refused where the definition is read rather than queued forever.
/// </para>
/// </summary>
public sealed class WorkerRequirement
{
    /// <summary>The most alternatives one requirement may carry (see the class remarks).</summary>
    public const int MaxDisjuncts = 64;

    /// <summary>
    /// The longest expression text one requirement accepts. It is the same bound as
    /// <see cref="MaxStorageFormLength"/> on purpose: a stored form has to be readable again (the claim loop
    /// re-parses the run's normalized column), so a parser that accepted less than it can write would refuse
    /// its own output and silently fall back to the legacy tags.
    /// </summary>
    public const int MaxExpressionLength = MaxStorageFormLength;

    /// <summary>The longest label atom one requirement accepts (the tag column's own limit).</summary>
    public const int MaxAtomLength = 64;

    /// <summary>
    /// The longest stored form one requirement may produce. <b>A policy bound, not a column width</b>: the
    /// column is unbounded <c>text</c> on purpose, so widening it must never be taken as a reason to raise
    /// this number (or narrowing it as a reason to trust the database instead). Raise it only with a decision
    /// about how large a requirement an author may write; the alternative - letting the column decide - is what
    /// turned a readable "simplify this requirement" into a database error about a column. <see cref="MaxDisjuncts"/> bounds the *number* of
    /// alternatives, not their size: sixty-four clauses of sixty-four-character atoms is tens of thousands of
    /// characters, so the stored form needs its own bound and its own message - a database complaining about
    /// a column is not an answer an author can act on. The column itself is unbounded <c>text</c>; this is the
    /// product rule, checked before anything is written.
    /// </summary>
    public const int MaxStorageFormLength = 4_000;

    /// <summary>
    /// How far the parser may expand before it gives up. Larger than <see cref="MaxDisjuncts"/> so that a
    /// requirement that is merely too large is reported as <see cref="TooComplex"/> rather than as
    /// unreadable - the difference is what an author needs to know.
    /// </summary>
    private const int ParseCeiling = MaxDisjuncts * 8;

    private static readonly char[] AtomPunctuation = ['.', '_', ':', '-', '/'];

    private readonly IReadOnlyList<IReadOnlyDictionary<string, bool>> _disjuncts;

    private WorkerRequirement(
        string expression,
        IReadOnlyList<IReadOnlyDictionary<string, bool>> disjuncts)
    {
        Expression = expression;
        _disjuncts = disjuncts;
        ReadableForm = RenderReadable(disjuncts);
        StorageForm = RenderStorage(disjuncts);
    }

    /// <summary>The expression exactly as it was written (empty for <see cref="None"/> and for a legacy tag list).</summary>
    public string Expression { get; }

    /// <summary>The human form: <c>linux and arm64</c>, <c>linux and (arm64 or x64)</c>, <c>not windows</c>.</summary>
    public string ReadableForm { get; }

    /// <summary>The stored form: canonical DNF, empty when there is no requirement.</summary>
    public string StorageForm { get; }

    /// <summary>Whether this requirement asks for nothing at all (every Worker satisfies it).</summary>
    public bool IsNone => _disjuncts.Count == 1 && _disjuncts[0].Count == 0;

    /// <summary>Whether no Worker can ever satisfy this requirement (<c>linux &amp;&amp; !linux</c>).</summary>
    public bool IsContradiction => _disjuncts.Count == 0 && !IsNone;

    /// <summary>The requirement that asks for nothing.</summary>
    public static WorkerRequirement None { get; } =
        new(string.Empty, [new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)]);

    /// <summary>The atoms this requirement names, in readable order (for a page or a diagnostic).</summary>
    public IEnumerable<string> Atoms =>
        _disjuncts.SelectMany(clause => clause.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(atom => atom, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses an expression. A <see langword="null"/>/blank text is <see cref="None"/>; a comma list is the
    /// conjunction of its atoms (the shape every stored tag column already has, so an old row keeps meaning
    /// exactly what it meant); anything else is the expression grammar
    /// <c>or := and ('||' and)*</c>, <c>and := factor ('&amp;&amp;' factor)*</c>,
    /// <c>factor := '!' factor | atom | '(' or ')'</c>.
    /// </summary>
    public static bool TryParse(string? text, out WorkerRequirement requirement, out string? error)
    {
        requirement = None;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var trimmed = text.Trim();
        if (trimmed.Length > MaxExpressionLength)
        {
            error = TooLong;
            return false;
        }

        // No operators at all: the legacy comma list (and the Lua table's shape). Every entry must match.
        if (!trimmed.Contains("&&", StringComparison.Ordinal)
            && !trimmed.Contains("||", StringComparison.Ordinal)
            && !trimmed.Contains('!')
            && !trimmed.Contains('(')
            && trimmed.Contains(','))
        {
            requirement = FromTags(trimmed);
            return true;
        }

        var tokens = Tokenize(trimmed, out var atoms);
        if (tokens is null)
        {
            error = InvalidExpression;
            return false;
        }

        var index = 0;
        var parsed = ParseOr(tokens, ref index, atoms);
        if (parsed is null || index != tokens.Count)
        {
            error = InvalidExpression;
            return false;
        }

        var disjuncts = parsed;
        if (disjuncts.Count > MaxDisjuncts)
        {
            error = TooComplex;
            return false;
        }

        var result = disjuncts.Count == 0
            ? new WorkerRequirement(trimmed, [])
            : new WorkerRequirement(trimmed, disjuncts);

        if (result.StorageForm.Length > MaxStorageFormLength)
        {
            error = TooLarge;
            return false;
        }

        requirement = result;
        return true;
    }

    /// <summary>
    /// The conjunction of a comma-separated tag list - the stored shape of every requirement written before
    /// expressions existed. An entry may carry a leading <c>!</c> (<c>"!windows"</c>) for symmetry with the
    /// expression grammar; the plain entries are what an old row holds.
    /// </summary>
    public static WorkerRequirement FromTags(string? tags)
    {
        var clause = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (tags ?? string.Empty).Split(','))
        {
            var tag = raw.Trim();
            if (tag.Length == 0)
            {
                continue;
            }

            var negated = tag.StartsWith('!');
            var atom = negated ? tag[1..].Trim() : tag;
            if (atom.Length == 0)
            {
                continue;
            }

            // A list that names the same atom both ways can never hold - that is a contradiction, and the
            // caller sees it through SatisfiedBy/IsContradiction rather than a silent "last one wins".
            if (clause.TryGetValue(atom, out var existing) && existing != !negated)
            {
                return new WorkerRequirement(tags!.Trim(), []);
            }

            clause[atom] = !negated;
        }

        if (clause.Count == 0)
        {
            return None;
        }

        return new WorkerRequirement(string.Empty, [clause]);
    }

    /// <summary>
    /// Whether a Worker carrying <paramref name="workerTags"/> satisfies this requirement. A Worker with no
    /// tags satisfies <see cref="None"/> and nothing else - the rule every trigger already applies today.
    /// </summary>
    public bool SatisfiedBy(IEnumerable<string>? workerTags)
    {
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (workerTags is not null)
        {
            foreach (var tag in workerTags)
            {
                var trimmed = tag.Trim();
                if (trimmed.Length > 0)
                {
                    available.Add(trimmed);
                }
            }
        }

        foreach (var clause in _disjuncts)
        {
            var matched = true;
            foreach (var (atom, mustBePresent) in clause)
            {
                if (available.Contains(atom) != mustBePresent)
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The union of two requirements: what both ask for, at once (a Worker must satisfy this <b>and</b>
    /// <paramref name="other"/>). Returns <see langword="null"/> when the union would exceed
    /// <see cref="MaxDisjuncts"/>.
    /// </summary>
    public WorkerRequirement? And(WorkerRequirement other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (IsNone)
        {
            return other;
        }

        if (other.IsNone)
        {
            return this;
        }

        var clauses = new List<IReadOnlyDictionary<string, bool>>();
        foreach (var left in _disjuncts)
        {
            foreach (var right in other._disjuncts)
            {
                var merged = new Dictionary<string, bool>(left, StringComparer.OrdinalIgnoreCase);
                var consistent = true;
                foreach (var (atom, mustBePresent) in right)
                {
                    if (merged.TryGetValue(atom, out var existing) && existing != mustBePresent)
                    {
                        consistent = false;
                        break;
                    }

                    merged[atom] = mustBePresent;
                }

                if (consistent)
                {
                    clauses.Add(merged);
                }
            }
        }

        if (clauses.Count > MaxDisjuncts)
        {
            return null;
        }

        var expression = IsNone || other.IsNone
            ? string.Empty
            : $"{StorageForm}&&({other.StorageForm})";
        return clauses.Count == 0
            ? new WorkerRequirement(expression, [])
            : new WorkerRequirement(expression, Normalize(clauses));
    }

    /// <summary>The labels this requirement names that <paramref name="workerTags"/> does not satisfy, for a page.</summary>
    public IReadOnlyList<string> UnsatisfiedAtoms(IEnumerable<string>? workerTags)
    {
        var available = new HashSet<string>((workerTags ?? []).Select(tag => tag.Trim()), StringComparer.OrdinalIgnoreCase);
        if (SatisfiedBy(available))
        {
            return [];
        }

        // Every clause failed: name the positive atoms of the first one (the common shape is a single
        // conjunction, where that is exactly the missing label).
        return _disjuncts.Count == 0
            ? []
            : _disjuncts[0]
                .Where(atom => atom.Value && !available.Contains(atom.Key))
                .Select(atom => atom.Key)
                .OrderBy(atom => atom, StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    /// <inheritdoc />
    public override string ToString() => ReadableForm;

    /// <summary>The refusal text for an over-long expression.</summary>
    public static readonly string TooLong =
        $"The worker requirement is longer than {MaxExpressionLength} characters.";

    /// <summary>The refusal text for an expression the grammar does not read.</summary>
    public const string InvalidExpression =
        "The worker requirement is not readable. Use labels joined by && (all), || (any) or ! (not), "
        + "for example linux && (arm64 || x64) or !windows, or a comma-separated list such as linux,arm64.";

    /// <summary>The refusal text for a stored form past <see cref="MaxStorageFormLength"/>.</summary>
    public static readonly string TooLarge =
        $"The worker requirement expands to more than {MaxStorageFormLength} characters once normalized. "
        + "Simplify it: fewer alternatives, or shorter groups.";

    /// <summary>The refusal text for a requirement with too many alternatives.</summary>
    public static readonly string TooComplex =
        $"The worker requirement has more than {MaxDisjuncts} alternatives. Simplify its || and ! groups; "
        + "a requirement that large cannot be checked against Workers cheaply.";

    // ------------------------------------------------------------------
    // Parsing
    // ------------------------------------------------------------------

    private enum TokenKind
    {
        Atom,
        And,
        Or,
        Not,
        Open,
        Close,
    }

    private readonly record struct Token(TokenKind Kind, int AtomIndex);

    private static List<Token>? Tokenize(string text, out List<string> atoms)
    {
        atoms = [];
        var tokens = new List<Token>();

        for (var position = 0; position < text.Length;)
        {
            var current = text[position];
            if (char.IsWhiteSpace(current))
            {
                position++;
                continue;
            }

            if (current == '&' && position + 1 < text.Length && text[position + 1] == '&')
            {
                tokens.Add(new Token(TokenKind.And, -1));
                position += 2;
                continue;
            }

            if (current == '|' && position + 1 < text.Length && text[position + 1] == '|')
            {
                tokens.Add(new Token(TokenKind.Or, -1));
                position += 2;
                continue;
            }

            if (current == '!')
            {
                tokens.Add(new Token(TokenKind.Not, -1));
                position++;
                continue;
            }

            if (current == '(')
            {
                tokens.Add(new Token(TokenKind.Open, -1));
                position++;
                continue;
            }

            if (current == ')')
            {
                tokens.Add(new Token(TokenKind.Close, -1));
                position++;
                continue;
            }

            var start = position;
            while (position < text.Length
                && (char.IsLetterOrDigit(text[position]) || Array.IndexOf(AtomPunctuation, text[position]) >= 0))
            {
                position++;
            }

            if (position == start)
            {
                return null;
            }

            var atom = text[start..position];
            if (atom.Length > MaxAtomLength)
            {
                return null;
            }

            atoms.Add(atom);
            tokens.Add(new Token(TokenKind.Atom, atoms.Count - 1));
        }

        return tokens;
    }

    /// <summary>The DNF a sub-expression normalizes to, or null when it does not parse.</summary>
    private static List<IReadOnlyDictionary<string, bool>>? ParseOr(List<Token> tokens, ref int index, List<string> atoms)
    {
        var left = ParseAnd(tokens, ref index, atoms);
        if (left is null)
        {
            return null;
        }

        while (index < tokens.Count && tokens[index].Kind == TokenKind.Or)
        {
            index++;
            var right = ParseAnd(tokens, ref index, atoms);
            if (right is null)
            {
                return null;
            }

            left = [.. left, .. right];
            if (left.Count > ParseCeiling)
            {
                return null;
            }
        }

        return left;
    }

    private static List<IReadOnlyDictionary<string, bool>>? ParseAnd(List<Token> tokens, ref int index, List<string> atoms)
    {
        var left = ParseFactor(tokens, ref index, atoms);
        if (left is null)
        {
            return null;
        }

        while (index < tokens.Count && tokens[index].Kind == TokenKind.And)
        {
            index++;
            var right = ParseFactor(tokens, ref index, atoms);
            if (right is null)
            {
                return null;
            }

            var product = new List<IReadOnlyDictionary<string, bool>>();
            foreach (var first in left)
            {
                foreach (var second in right)
                {
                    var merged = new Dictionary<string, bool>(first, StringComparer.OrdinalIgnoreCase);
                    var consistent = true;
                    foreach (var (atom, mustBePresent) in second)
                    {
                        if (merged.TryGetValue(atom, out var existing) && existing != mustBePresent)
                        {
                            consistent = false;
                            break;
                        }

                        merged[atom] = mustBePresent;
                    }

                    if (consistent)
                    {
                        product.Add(merged);
                    }
                }
            }

            if (product.Count > ParseCeiling)
            {
                return null;
            }

            left = product;
        }

        return left;
    }

    private static List<IReadOnlyDictionary<string, bool>>? ParseFactor(List<Token> tokens, ref int index, List<string> atoms)
    {
        if (index >= tokens.Count)
        {
            return null;
        }

        var token = tokens[index];
        switch (token.Kind)
        {
            case TokenKind.Not:
                index++;
                var negated = ParseFactor(tokens, ref index, atoms);
                return negated is null
                    ? null
                    : Negate(negated);

            case TokenKind.Open:
                index++;
                var inner = ParseOr(tokens, ref index, atoms);
                if (inner is null || index >= tokens.Count || tokens[index].Kind != TokenKind.Close)
                {
                    return null;
                }

                index++;
                return inner;

            case TokenKind.Atom:
                index++;
                return [new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    [atoms[token.AtomIndex]] = true,
                }];

            default:
                return null;
        }
    }

    /// <summary>The negation of a DNF: De Morgan, clause by clause, then re-normalized.</summary>
    private static List<IReadOnlyDictionary<string, bool>> Negate(List<IReadOnlyDictionary<string, bool>> disjuncts)
    {
        // !(c1 || c2) == !c1 && !c2, and !(a && !b) == !a || b.
        var result = new List<IReadOnlyDictionary<string, bool>> { new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) };
        foreach (var clause in disjuncts)
        {
            var negatedClause = clause
                .Select(atom => new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { [atom.Key] = !atom.Value })
                .ToList();

            var product = new List<IReadOnlyDictionary<string, bool>>();
            foreach (var left in result)
            {
                foreach (var right in negatedClause)
                {
                    var merged = new Dictionary<string, bool>(left, StringComparer.OrdinalIgnoreCase);
                    var consistent = true;
                    foreach (var (atom, mustBePresent) in right)
                    {
                        if (merged.TryGetValue(atom, out var existing) && existing != mustBePresent)
                        {
                            consistent = false;
                            break;
                        }

                        merged[atom] = mustBePresent;
                    }

                    if (consistent)
                    {
                        product.Add(merged);
                    }
                }
            }

            result = product;
            if (result.Count > ParseCeiling)
            {
                return [];
            }
        }

        return Normalize(result);
    }

    /// <summary>Sorts atoms inside a clause and clauses among themselves, so equal requirements render equally.</summary>
    private static List<IReadOnlyDictionary<string, bool>> Normalize(List<IReadOnlyDictionary<string, bool>> clauses) =>
        [.. clauses
            .Select(clause => (IReadOnlyDictionary<string, bool>)clause
                .OrderBy(atom => atom.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(atom => atom.Key, atom => atom.Value, StringComparer.OrdinalIgnoreCase))
            .OrderBy(clause => RenderStorage([clause]), StringComparer.Ordinal)];

    // ------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------

    private static string RenderStorage(IReadOnlyList<IReadOnlyDictionary<string, bool>> disjuncts)
    {
        if (disjuncts.Count == 1 && disjuncts[0].Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var clause in disjuncts)
        {
            if (builder.Length > 0)
            {
                builder.Append("||");
            }

            if (disjuncts.Count > 1)
            {
                builder.Append('(');
            }

            var first = true;
            foreach (var (atom, mustBePresent) in clause.OrderBy(atom => atom.Key, StringComparer.Ordinal))
            {
                if (!first)
                {
                    builder.Append("&&");
                }

                if (!mustBePresent)
                {
                    builder.Append('!');
                }

                builder.Append(atom);
                first = false;
            }

            if (disjuncts.Count > 1)
            {
                builder.Append(')');
            }
        }

        return builder.ToString();
    }

    private static string RenderReadable(IReadOnlyList<IReadOnlyDictionary<string, bool>> disjuncts)
    {
        if (disjuncts.Count == 1 && disjuncts[0].Count == 0)
        {
            return string.Empty;
        }

        var alternatives = disjuncts
            .Select(clause =>
            {
                var parts = clause
                    .OrderBy(atom => atom.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(atom => atom.Value ? atom.Key : $"not {atom.Key}")
                    .ToArray();
                var joined = string.Join(" and ", parts);
                return alternativesCount(disjuncts) > 1 && parts.Length > 1 ? $"({joined})" : joined;
            })
            .ToArray();

        return string.Join(" or ", alternatives);

        static int alternativesCount(IReadOnlyList<IReadOnlyDictionary<string, bool>> inner) => inner.Count;
    }
}
