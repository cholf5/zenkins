namespace Zenkins.Worker.Pipelines;

using System.Text;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Secrets;
using Zenkins.Worker.Git;

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
/// Substitutes <see cref="StepReference"/> markers in a step's environment values and argv (card F-015,
/// extended to the secret kinds by card F-040).
/// <para>
/// Resolution happens on the host, on the step's boundary, in two passes: environment references first
/// (against the step's composed environment, so a value may name another variable), then secrets - each of
/// which is fetched through <see cref="IRunSecretSource"/> once per name, decoded by its <b>kind</b>, and
/// <b>registered with the run's redaction filter in the same instant</b>, before the step starts and therefore
/// before any line that could contain it is emitted.
/// </para>
/// <para>
/// <b>What each kind substitutes (card F-040).</b> A <c>text</c> secret substitutes its value for
/// <c>${secret:NAME}</c>. A <c>usernamePassword</c> secret substitutes one half for
/// <c>${secret:NAME.username}</c> / <c>.password</c> - each half resolvable on its own, because
/// <c>docker login</c> wants one in an argument and the other on stdin. A <c>file</c> secret substitutes a
/// <b>path</b> for <c>${secretFile:NAME}</c>, and the material is written inside this run's own tree by
/// <see cref="RunSecretFiles"/>. Asking for a shape the secret does not have is refused with the parser's own
/// explanation (<see cref="SecretReference.MismatchMessage"/>), never silently emptied.
/// </para>
/// <para>
/// Anything that is not a well-formed reference is left exactly as it is: a stray <c>${</c> is a string,
/// not a parse error, so a step's argv is never rewritten by accident.
/// </para>
/// </summary>
public sealed class StepValueResolver(
    GitLogFilter redactor,
    IRunSecretSource secrets,
    RunSecretFiles? files = null)
{
    /// <summary>Guards against a value that references itself (or a cycle of references).</summary>
    private const int MaxExpansionDepth = 4;

    private readonly GitLogFilter _redactor = redactor;
    private readonly IRunSecretSource _secrets = secrets;
    private readonly RunSecretFiles? _files = files;

    /// <summary>
    /// Resolves the environment references in <paramref name="value"/> against
    /// <paramref name="environment"/>. Secret references are deliberately <b>not</b> resolved here: this
    /// pass exists for the condition evaluation, which must not fetch anything.
    /// </summary>
    public static string ResolveEnvironment(string value, IReadOnlyDictionary<string, string?> environment) =>
        Expand(value, environment, secretValue: null);

    /// <summary>
    /// Fully resolves <paramref name="value"/>: environment references against
    /// <paramref name="environment"/>, secret references through the run's source (registering each revealed
    /// value with the redaction filter as it arrives).
    /// </summary>
    /// <exception cref="StepPreparationException">
    /// A secret reference could not be resolved (unknown name, run not ours, server refused), the reference
    /// asks for a shape the secret does not have, or a file secret could not be materialised. The message
    /// names the <b>reference</b>; it never carries a value or a file's content.
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

        // Fetch once per name, decode once per name: a pair referenced as .username and .password costs one
        // request, and both halves are read from the same decoded payload.
        var payloads = new Dictionary<string, SecretPayload>(StringComparer.Ordinal);
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var reference in References(value))
        {
            if (!payloads.TryGetValue(reference.Name, out var payload))
            {
                RunSecretResult result;
                try
                {
                    result = await _secrets.FetchAsync(reference.Name, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception failure)
                {
                    throw new StepPreparationException(
                        $"secret(\"{reference.Name}\") could not be fetched: {failure.Message}",
                        Zenkins.Shared.Pipelines.PipelineStepFailureReasons.SecretUnavailable,
                        failure);
                }

                if (result.Outcome != RunSecretLookup.Found || result.Value is null)
                {
                    throw new StepPreparationException(
                        $"secret(\"{reference.Name}\") is not available in this run's organization.",
                        Zenkins.Shared.Pipelines.PipelineStepFailureReasons.SecretUnavailable);
                }

                payload = Decode(reference.Name, result.Kind, result.Value);
                payloads[reference.Name] = payload;

                // Registered the instant it is known: from here on no line this run emits can carry it. Only
                // the material goes in - a pair's username and a file's path stay printable.
                Register(payload);
            }

            if (!replacements.ContainsKey(reference.Recorded))
            {
                replacements[reference.Recorded] = Substitute(reference, payload);
            }
        }

        return Expand(
            value,
            environment,
            recorded => replacements.TryGetValue(recorded, out var replacement) ? replacement : null);
    }

    /// <summary>
    /// The one place a fetched payload becomes its declared shape. A payload that does not parse is a storage
    /// or protocol fault, so it fails the step with a message that names the secret and never echoes the
    /// plaintext.
    /// </summary>
    private static SecretPayload Decode(string name, string? kind, string plaintext)
    {
        var declared = kind ?? SecretKinds.Default;
        try
        {
            return SecretPayload.Decode(declared, plaintext);
        }
        catch (InvalidOperationException failure)
        {
            throw new StepPreparationException(
                $"secret(\"{name}\") is stored as '{declared}' but its value is not that shape: {failure.Message}",
                Zenkins.Shared.Pipelines.PipelineStepFailureReasons.SecretUnavailable,
                failure);
        }
    }

    /// <summary>
    /// Registers the material a reveal makes available (card F-040): a text secret's value, a pair's
    /// <b>password</b> (a username is an identity, not a secret - TC-001 allows it to be visible), and a file
    /// secret's <b>content</b>. A file's <b>path</b> is deliberately not registered: it is what the step must
    /// print, and it carries no material.
    /// </summary>
    private void Register(SecretPayload payload)
    {
        switch (payload.Kind)
        {
            case SecretKinds.UsernamePassword:
                _redactor.Register(payload.Password);
                break;

            case SecretKinds.File:
                _redactor.Register(payload.Content);
                break;

            default:
                _redactor.Register(payload.Value);
                break;
        }
    }

    /// <summary>
    /// The text one reference substitutes: the value, one half of the pair, or the path of the materialised
    /// file. A reference that asks for a shape the secret does not have is a preparation failure carrying the
    /// parser's explanation.
    /// </summary>
    private string Substitute(SecretReference reference, SecretPayload payload)
    {
        if (!reference.IsAllowedFor(payload.Kind))
        {
            throw new StepPreparationException(
                reference.MismatchMessage(payload.Kind),
                Zenkins.Shared.Pipelines.PipelineStepFailureReasons.SecretUnavailable);
        }

        return payload.Kind switch
        {
            SecretKinds.Text => payload.Value ?? string.Empty,
            SecretKinds.UsernamePassword => reference.Part == SecretPart.Username
                ? payload.Username ?? string.Empty
                : payload.Password ?? string.Empty,
            SecretKinds.File => Materialize(reference.Name, payload.Content ?? string.Empty),
            _ => throw new StepPreparationException(
                $"secret(\"{reference.Name}\") has the unknown kind '{payload.Kind}' and cannot be resolved.",
                Zenkins.Shared.Pipelines.PipelineStepFailureReasons.SecretUnavailable),
        };
    }

    /// <summary>
    /// Writes a file secret into this run's own tree and answers its path. No work directory means no
    /// deliverable location, which is a failed step rather than a file dropped somewhere shared.
    /// </summary>
    private string Materialize(string name, string content)
    {
        if (_files is null)
        {
            throw new StepPreparationException(
                $"${{secretFile:{name}}} cannot be resolved: this run has no work directory to write it in.",
                Zenkins.Shared.Pipelines.PipelineStepFailureReasons.SecretUnavailable);
        }

        try
        {
            return _files.Materialize(name, content);
        }
        catch (RunSecretFileException failure)
        {
            throw new StepPreparationException(
                failure.Message,
                Zenkins.Shared.Pipelines.PipelineStepFailureReasons.SecretUnavailable,
                failure);
        }
    }

    /// <summary>
    /// One expansion pass over <paramref name="value"/>. <paramref name="secretValue"/> returns the text one
    /// <b>recorded reference</b> (<c>NAME</c>, <c>NAME.username</c>, <c>NAME.password</c>, <c>file:NAME</c>)
    /// resolves to, or null when secrets are not resolved in this pass.
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

            var inner = value[nameStart..end];

            if (kind == StepReference.EnvironmentPrefix)
            {
                if (!StepReference.IsValidName(inner))
                {
                    builder.Append(value[position]);
                    position++;
                    continue;
                }

                builder.Append(ExpandEnvironment(inner, environment, depth: 0));

                // An environment reference is a whole marker: skip past it.
                position = end + 1;
                continue;
            }

            // A secret reference is a whole marker too, but only when it parses AND this pass resolves
            // secrets. Either way an unresolved one is left verbatim rather than emptied.
            if (!SecretReference.TryParse(kind, inner, out var reference)
                || secretValue is null
                || secretValue(reference.Recorded) is not { } replacement)
            {
                builder.Append(value[position]);
                position++;
                continue;
            }

            builder.Append(replacement);
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
    /// The well-formed secret references a value makes, in order of first appearance. One scanner
    /// (<see cref="StepReference.Match"/> + <see cref="SecretReference.TryParse"/>) decides both what a value
    /// references and what each reference means, so the fetch list and the substitution cannot disagree.
    /// </summary>
    private static IEnumerable<SecretReference> References(string value)
    {
        var position = 0;
        while (position < value.Length)
        {
            var (prefix, nameStart) = StepReference.Match(value, position);
            if (prefix is not (StepReference.SecretPrefix or StepReference.SecretFilePrefix))
            {
                position++;
                continue;
            }

            var end = value.IndexOf(StepReference.Terminator, nameStart);
            if (end < 0)
            {
                yield break;
            }

            if (SecretReference.TryParse(prefix, value[nameStart..end], out var reference))
            {
                yield return reference;
            }

            position = end + 1;
        }
    }
}
