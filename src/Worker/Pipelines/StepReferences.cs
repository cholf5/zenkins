using System.Text;
using Zenkins.Shared.Pipelines;
using Zenkins.Worker.Git;

namespace Zenkins.Worker.Pipelines;

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
