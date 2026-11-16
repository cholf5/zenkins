namespace Zenkins.Shared.Pipelines;

/// <summary>
/// The deferred-binding syntax of a frozen step (card F-015).
/// <para>
/// <b>Why a placeholder and not a value.</b> A pipeline is described once, before any step runs
/// (<c>docs/lua-runtime.md</c> §6.1), and describing it does no I/O: the run's environment does not exist
/// yet and no secret may be decrypted then. So the SDK's <c>env("NAME")</c> and <c>secret("NAME")</c> are
/// <b>deferred</b>: they return a reference - <c>${env:NAME}</c> / <c>${secret:NAME}</c> - which the C#
/// executor substitutes in the step's argv, script text and environment <b>immediately before the step
/// starts</b>, on the full precedence chain (inherited process environment &lt; pipeline <c>env</c> &lt;
/// step <c>env</c> &lt; the server-provided run environment &lt; the decrypted secret).
/// </para>
/// <para>
/// <b>Consequence, and it is deliberate:</b> a script that does string arithmetic with a reference, or
/// prints one while being described, sees the <em>reference</em> rather than the value
/// (<c>docs/lua-runtime.md</c> §7 records this as a known limitation). Putting the reference into an
/// argv entry, a script text or an <c>env</c> value - the three places the executor substitutes - is what
/// yields the real value, and it keeps the definition phase from ever holding plaintext.
/// </para>
/// <para>
/// <b>Why this lives in Shared (card F-020).</b> The Worker resolves the references at the step boundary;
/// the Server scans a Freestyle job's frozen script texts with the same scanner when it records which
/// secrets a spec references (the D-006 capture). One scanner, so "what the Server recorded" and "what
/// the Worker substitutes" cannot drift apart.
/// </para>
/// </summary>
public static class StepReference
{
    /// <summary>The prefix of a secret reference (<c>${secret:NAME}</c>).</summary>
    public const string SecretPrefix = "${secret:";

    /// <summary>The prefix of an environment reference (<c>${env:NAME}</c>).</summary>
    public const string EnvironmentPrefix = "${env:";

    /// <summary>The terminator of either reference.</summary>
    public const char Terminator = '}';

    /// <summary>The reference that stands for <c>secret(name)</c>.</summary>
    public static string ForSecret(string name) => SecretPrefix + name + Terminator;

    /// <summary>The reference that stands for <c>env(name)</c>.</summary>
    public static string ForEnvironment(string name) => EnvironmentPrefix + name + Terminator;

    /// <summary>
    /// Whether <paramref name="name"/> is a valid reference target: an environment-variable-shaped name,
    /// which is also what <c>SecretValidation</c> accepts for a stored secret.
    /// <para>
    /// <b>Shape only, no length bound - deliberately unchanged.</b> The 64-character bound belongs to the
    /// <em>recorded</em> secret reference list (<see cref="PipelineSpecSchema.IsSecretReferenceName"/>), because a
    /// stored secret cannot be longer; <c>env()</c> and the <c>${env:…}</c> scanner keep the shape rule they have
    /// always had, so a long environment variable name stays legal (card D-006 does not change F-015).
    /// </para>
    /// </summary>
    public static bool IsValidName(string? name) => PipelineSpecSchema.IsReferenceNameShape(name);

    /// <summary>
    /// The secret names a value references, in order of first appearance (card D-006 uses it to record what
    /// a frozen spec can resolve; the resolver uses it to fetch). Shape only: a name no stored secret could
    /// have is filtered where the record is built, not here - the resolver's behaviour is unchanged.
    /// </summary>
    public static IReadOnlyList<string> SecretNamesIn(string? value)
    {
        if (value is null || !value.Contains(SecretPrefix, StringComparison.Ordinal))
        {
            return [];
        }

        var names = new List<string>();
        var position = 0;
        while (position < value.Length)
        {
            var (kind, nameStart) = Match(value, position);
            if (kind is null)
            {
                position++;
                continue;
            }

            var end = value.IndexOf(Terminator, nameStart);
            if (end < 0)
            {
                break;
            }

            var name = value[nameStart..end];
            if (kind == SecretPrefix && IsValidName(name))
            {
                names.Add(name);
            }

            position = end + 1;
        }

        return names;
    }

    /// <summary>Whether <paramref name="value"/> contains at least one reference.</summary>
    public static bool Contains(string? value) =>
        value is not null
        && (value.Contains(SecretPrefix, StringComparison.Ordinal)
            || value.Contains(EnvironmentPrefix, StringComparison.Ordinal));

    /// <summary>
    /// Whether a reference starts at <paramref name="position"/> and where its name starts. The one
    /// scanner both the resolver (Worker) and <see cref="SecretNamesIn"/> use, so "what a value references"
    /// and "what the executor substitutes" cannot drift apart. Public because the resolver lives in the
    /// Worker assembly and Shared carries no <c>InternalsVisibleTo</c>.
    /// </summary>
    public static (string? Prefix, int NameStart) Match(string value, int position)
    {
        if (string.CompareOrdinal(value, position, SecretPrefix, 0, SecretPrefix.Length) == 0)
        {
            return (SecretPrefix, position + SecretPrefix.Length);
        }

        if (string.CompareOrdinal(value, position, EnvironmentPrefix, 0, EnvironmentPrefix.Length) == 0)
        {
            return (EnvironmentPrefix, position + EnvironmentPrefix.Length);
        }

        return (null, position);
    }
}
