using Xunit;
using Zenkins.Shared.Workers;

namespace Zenkins.Tests.Workers;

/// <summary>
/// Card F-033's shared requirement type: the one parser, the one normal form, and the three questions every
/// caller asks of it (satisfied, contradiction, union). The three call sites - the claim loop, the trigger's
/// satisfiability check and the definition-observation queue - hold no rules of their own.
/// </summary>
public sealed class WorkerRequirementTests
{
    [Fact]
    public void An_expression_is_satisfied_exactly_when_the_grammar_says_so()
    {
        var requirement = Parse("linux && (arm64 || x64)");

        Assert.True(requirement.SatisfiedBy(["linux", "arm64"]));
        Assert.True(requirement.SatisfiedBy(["linux", "x64"]));
        Assert.True(requirement.SatisfiedBy(["x64", "linux", "extra"]));

        Assert.False(requirement.SatisfiedBy(["linux"]));
        Assert.False(requirement.SatisfiedBy(["arm64", "x64"]));
        Assert.False(requirement.SatisfiedBy([]));
    }

    [Fact]
    public void Not_excludes_a_label_and_keeps_working_with_no_tags_at_all()
    {
        var requirement = Parse("!windows");

        Assert.True(requirement.SatisfiedBy([]));
        Assert.True(requirement.SatisfiedBy(["linux"]));
        Assert.False(requirement.SatisfiedBy(["windows"]));
    }

    [Fact]
    public void The_stored_form_round_trips_to_the_same_requirement()
    {
        // Storage is DNF, the page shows prose - and the stored text must parse back to the same answers,
        // including for the shapes that expand (|| under &&) and for a negation.
        foreach (var expression in new[]
        {
            "linux && (arm64 || x64)",
            "!windows",
            "linux && !windows",
            "(linux || windows) && (arm64 || x64)",
        })
        {
            var parsed = Parse(expression);
            var reparsed = Parse(parsed.StorageForm);

            foreach (var tags in new[]
            {
                Array.Empty<string>(),
                ["linux"],
                ["linux", "arm64"],
                ["linux", "x64"],
                ["windows"],
                ["windows", "arm64"],
            })
            {
                Assert.Equal(parsed.SatisfiedBy(tags), reparsed.SatisfiedBy(tags));
            }
        }
    }

    [Fact]
    public void A_comma_list_is_the_conjunction_of_its_atoms_so_an_old_row_keeps_its_meaning()
    {
        // Every requirement column written before this card is a comma list. It must mean exactly what the
        // claim loop meant by it: all of the atoms, case-insensitively.
        var legacy = TreeTags("Linux, arm64");

        Assert.Equal("Linux&&arm64", legacy.StorageForm);
        Assert.True(legacy.SatisfiedBy(["linux", "arm64"]));
        Assert.False(legacy.SatisfiedBy(["linux"]));
        Assert.False(legacy.SatisfiedBy([]));
    }

    [Fact]
    public void A_worker_with_no_tags_satisfies_no_requirement_and_the_absence_of_one()
    {
        Assert.True(WorkerRequirement.None.SatisfiedBy([]));
        Assert.True(WorkerRequirement.None.SatisfiedBy(["anything"]));
        Assert.True(WorkerRequirement.None.IsNone);

        // ...and "no requirement" is not the empty expression: a requirement made only of negations is still
        // satisfied by a tagless worker, but a positive atom never is.
        Assert.False(Parse("linux").SatisfiedBy([]));
    }

    [Fact]
    public void A_contradiction_is_recognised_and_never_satisfied()
    {
        var contradiction = Parse("linux && !linux");

        Assert.True(contradiction.IsContradiction);
        Assert.False(contradiction.IsNone);
        Assert.False(contradiction.SatisfiedBy(["linux"]));
        Assert.False(contradiction.SatisfiedBy(["windows"]));
        Assert.False(contradiction.SatisfiedBy([]));

        // A list that names the same atom both ways is the same contradiction; two *different* atoms that no
        // machine carries both of (`windows` and `linux`) are not a logical contradiction - they are a
        // requirement no current Worker satisfies, which the trigger queues rather than refuses.
        Assert.True(TreeTags("linux,!linux").IsContradiction);

        var union = Parse("windows").And(Parse("linux"));
        Assert.NotNull(union);
        Assert.False(union!.IsContradiction);
        Assert.False(union.SatisfiedBy(["windows"]));
        Assert.False(union.SatisfiedBy(["linux"]));
        // ...and a machine that genuinely carries both labels does satisfy it: the boolean form has no
        // opinion about which labels usually go together, only the caller's tag catalogue does.
        Assert.True(union.SatisfiedBy(["windows", "linux"]));
    }

    [Fact]
    public void Too_many_alternatives_is_refused_rather_than_stored()
    {
        var expression = string.Join(
            " || ",
            Enumerable.Range(0, WorkerRequirement.MaxDisjuncts + 1).Select(index => $"tag{index}"));

        Assert.False(WorkerRequirement.TryParse(expression, out _, out var error));
        Assert.Equal(WorkerRequirement.TooComplex, error);

        // A conjunction of two big alternatives multiplies, and the product is what hits the bound.
        var left = string.Join(" || ", Enumerable.Range(0, WorkerRequirement.MaxDisjuncts).Select(index => $"a{index}"));
        Assert.False(WorkerRequirement.TryParse($"{left} || x", out var wide, out _) is false && wide.And(Parse("y")) is null);
    }

    [Fact]
    public void A_stored_form_just_inside_the_limit_is_accepted_and_one_atom_wider_is_refused()
    {
        // Four groups of two alternatives under && is 16 clauses, so the stored form's size is a function of
        // the atom length: 16 x (4 atoms + "&&" separators + the clause's parentheses) + the "||" joins.
        var fits = WideText(atomLength: 60);
        Assert.True(WorkerRequirement.TryParse(fits, out var accepted, out var acceptedError), acceptedError);
        Assert.Equal(3_998, accepted.StorageForm.Length);

        // The stored form has to be readable again (the claim loop re-parses the run's column): a parser that
        // accepted less than it can write would refuse its own output and fall back to the legacy tags.
        Assert.True(WorkerRequirement.TryParse(accepted.StorageForm, out var reparsed, out var reparsedError), reparsedError);
        Assert.Equal(accepted.StorageForm, reparsed.StorageForm);

        // One character per atom more (4 atoms x 16 clauses) crosses MaxStorageFormLength - refused by the
        // type with a message about the requirement, not by a database about a column.
        var tooWide = WideText(atomLength: 61);
        Assert.False(WorkerRequirement.TryParse(tooWide, out _, out var error));
        Assert.Equal(WorkerRequirement.TooLarge, error);
    }

    /// <summary>
    /// <c>(a{len}||b{len})&amp;&amp;(c{len}||d{len})&amp;&amp;(e{len}||f{len})&amp;&amp;(g{len}||h{len})</c> - 16 clauses whose
    /// stored size grows linearly with the atom length, which is what makes the boundary exact.
    /// </summary>
    private static string WideText(int atomLength)
    {
        string Atom(char letter) => new(letter, atomLength);

        var groups = new List<string>();
        for (var group = 0; group < 4; group++)
        {
            groups.Add($"({Atom((char)('a' + (group * 2)))}||{Atom((char)('b' + (group * 2)))})");
        }

        return string.Join("&&", groups);
    }

    private static WorkerRequirement Parse(string expression)
    {
        Assert.True(
            WorkerRequirement.TryParse(expression, out var requirement, out var error),
            $"'{expression}' did not parse: {error}");
        return requirement;
    }

    private static WorkerRequirement TreeTags(string tags) => WorkerRequirement.FromTags(tags);
}
