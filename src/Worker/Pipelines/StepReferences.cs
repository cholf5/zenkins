using System.Text;
using Zenkins.Shared.Pipelines;
using Zenkins.Worker.Git;

namespace Zenkins.Worker.Pipelines;

/// <summary>
/// The deferred-binding syntax of a frozen step (card F-015).
/// <para>
/// <b>Why a placeholder and not a value.</b> A pipeline is described once, before any step runs
/// (<c>docs/lua-runtime.md</c> §6.1), and describing it does no I/O: the run's environment does not exist
/// yet and no secret may be decrypted then. So the SDK's <c>env("NAME")</c> and <c>secret("NAME")</c> are
/// <b>deferred</b>: they return a reference - <c>${env:NAME}</c> / <c>${secret:NAME}</c> - which the C#
/// executor substitutes in the step's argv and environment <b>immediately before the step starts</b>, on
/// the full precedence chain (inherited process environment &lt; pipeline <c>env</c> &lt; step <c>env</c>
/// &lt; the server-provided run environment &lt; the decrypted secret).
/// </para>
/// <para>
/// <b>Consequence, and it is deliberate:</b> a script that does string arithmetic with a reference, or
/// prints one while being described, sees the <em>reference</em> rather than the value
/// (<c>docs/lua-runtime.md</c> §7 records this as a known limitation). Putting the reference into an
/// argv entry or an <c>env</c> value - the two places the executor substitutes - is what yields the real
/// value, and it keeps the definition phase from ever holding plaintext.
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
    /// scanner both the resolver and <see cref="SecretNamesIn"/> use, so "what a value references" and
    /// "what the executor substitutes" cannot drift apart.
    /// </summary>
    internal static (string? Prefix, int NameStart) Match(string value, int position)
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

/// <summary>
/// A step that could not be prepared: the step is reported <c>Failed</c> and the run log carries the
/// message (card F-015). This is not a process failure - no process started - so no exit code is invented;
/// <see cref="FailureReason"/> is one of <see cref="Zenkins.Shared.Pipelines.PipelineStepFailureReasons"/>.
/// <para>
/// The message names the <b>secret name</b> and never a value: it is written to the run log, which the
/// step's own step header would otherwise make awkward. The step's own process never sees it.
/// </para>
/// </summary>
public sealed class StepPreparationException(string message, string failureReason, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>The <c>PipelineStepFailureReasons</c> value the step is failed with.</summary>
    public string FailureReason { get; } = failureReason;
}

/// <summary>
/// Substitutes <see cref="StepReference"/> markers in a step's environment values and argv (card F-015).
/// <para>
/// Resolution happens on the host, on the step's boundary, in two passes: environment references first
/// (against the step's composed environment, so a value may name another variable), then secrets - each of
/// which is fetched through <see cref="IRunSecretSource"/>, decrypted there, and <b>registered with the
/// run's redaction filter in the same instant</b>, before the step starts and therefore before any line
/// that could contain it is emitted.
/// </para>
/// <para>
/// Anything that is not a well-formed reference is left exactly as it is: a stray <c>${</c> is a string,
/// not a parse error, so a step's argv is never rewritten by accident.
/// </para>
/// </summary>
public sealed class StepValueResolver(GitLogFilter redactor, IRunSecretSource secrets)
{
    /// <summary>Guards against a value that references itself (or a cycle of references).</summary>
    private const int MaxExpansionDepth = 4;

    private readonly GitLogFilter _redactor = redactor;
    private readonly IRunSecretSource _secrets = secrets;

    /// <summary>
    /// Resolves the environment references in <paramref name="value"/> against
    /// <paramref name="environment"/>. Secret references are deliberately <b>not</b> resolved here: this
    /// pass exists for the condition evaluation, which must not fetch anything.
    /// </summary>
    public static string ResolveEnvironment(string value, IReadOnlyDictionary<string, string?> environment) =>
        Expand(value, environment, secretValue: null);

    /// <summary>
    /// Fully resolves <paramref name="value"/>: environment references against
    /// <paramref name="environment"/>, secret references through the run's source (registering each value
    /// with the redaction filter as it arrives).
    /// </summary>
    /// <exception cref="StepPreparationException">
    /// A secret reference could not be resolved (unknown name, run not ours, server refused). The message
    /// names the <b>name</b>; it never carries a value.
    /// </exception>
    public async Task<string> ResolveAsync(
        string value,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        if (!StepReference.Contains(value))
        {
            return value;
        }

        // Collect the secret names first, so the fetch happens on values without holding a partial
        // substitution. A name repeated in the same value costs one cache hit.
        var cached = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in SecretNames(value))
        {
            if (cached.ContainsKey(name))
            {
                continue;
            }

            RunSecretResult result;
            try
            {
                result = await _secrets.FetchAsync(name, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception failure)
            {
                throw new StepPreparationException(
                    $"secret(\"{name}\") could not be fetched: {failure.Message}",
                    Zenkins.Shared.Pipelines.PipelineStepFailureReasons.SecretUnavailable,
                    failure);
            }

            if (result.Outcome != RunSecretLookup.Found || result.Value is null)
            {
                throw new StepPreparationException(
                    $"secret(\"{name}\") is not available in this run's organization.",
                    Zenkins.Shared.Pipelines.PipelineStepFailureReasons.SecretUnavailable);
            }

            // Registered the instant it is known: from here on no line this run emits can carry it.
            _redactor.Register(result.Value);
            cached[name] = result.Value;
        }

        return Expand(
            value,
            environment,
            name => cached.TryGetValue(name, out var secret) ? secret : null);
    }

    /// <summary>
    /// One expansion pass over <paramref name="value"/>. <paramref name="secretValue"/> returns the value
    /// for a secret name, or null when secrets are not resolved in this pass.
    /// </summary>
    private static string Expand(
        string value,
        IReadOnlyDictionary<string, string?> environment,
        Func<string, string?>? secretValue)
    {
        if (!StepReference.Contains(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var position = 0;
        while (position < value.Length)
        {
            var (kind, nameStart) = StepReference.Match(value, position);
            if (kind is null)
            {
                builder.Append(value[position]);
                position++;
                continue;
            }

            var end = value.IndexOf(StepReference.Terminator, nameStart);
            if (end < 0)
            {
                // unterminated: a plain string, never a parse error
                builder.Append(value[position]);
                position++;
                continue;
            }

            var name = value[nameStart..end];
            if (!StepReference.IsValidName(name))
            {
                builder.Append(value[position]);
                position++;
                continue;
            }

            if (kind == StepReference.EnvironmentPrefix)
            {
                builder.Append(ExpandEnvironment(name, environment, depth: 0));

                // An environment reference is a whole marker: skip past it.
                position = end + 1;
                continue;
            }

            if (secretValue is null || secretValue(name) is not { } secret)
            {
                // Unresolved in this pass - leave the reference verbatim rather than emptying it.
                builder.Append(value[position..(end + 1)]);
                position = end + 1;
                continue;
            }

            builder.Append(secret);
            position = end + 1;
        }

        return builder.ToString();
    }

    /// <summary>
    /// One environment value, following a reference chain (a variable whose value is itself a reference)
    /// up to <see cref="MaxExpansionDepth"/> hops. An unset variable resolves to the empty string - the same
    /// answer a shell gives <c>$NAME</c>, and the reason the run page shows the composed environment.
    /// </summary>
    private static string ExpandEnvironment(
        string name,
        IReadOnlyDictionary<string, string?> environment,
        int depth)
    {
        if (!environment.TryGetValue(name, out var value) || value is null)
        {
            return string.Empty;
        }

        if (depth >= MaxExpansionDepth || !StepReference.Contains(value))
        {
            return value;
        }

        // Expand a nested environment reference, but never a nested secret reference: a secret is only
        // ever fetched at the step boundary (ResolveAsync), never from inside another value.
        var expanded = Expand(value, environment, secretValue: null);
        return expanded == value
            ? value
            : Expand(expanded, environment, secretValue: null);
    }

    /// <summary>
    /// The well-formed secret names a value references (in order of first appearance).
    /// </summary>
    private static IEnumerable<string> SecretNames(string value) => StepReference.SecretNamesIn(value);
}
