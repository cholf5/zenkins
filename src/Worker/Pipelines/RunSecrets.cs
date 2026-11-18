namespace Zenkins.Worker.Pipelines;

/// <summary>Whether a run-scoped secret fetch found a value (card F-015).</summary>
public enum RunSecretLookup
{
    /// <summary>The organization has that name and this run may read it.</summary>
    Found = 0,

    /// <summary>Nothing to return: the name does not exist here, the run is not this worker's, or the
    /// server refused. Deliberately one value - the worker cannot distinguish the reasons either.</summary>
    NotFound = 1,
}

/// <summary>
/// The answer of one secret fetch: the decrypted payload and its <b>kind</b> on success, nothing otherwise
/// (card F-040). The kind is what <see cref="Zenkins.Shared.Secrets.SecretPayload.Decode"/> needs: the payload
/// of a pair is the length-prefixed pair document and the payload of a file is its bytes.
/// </summary>
public sealed record RunSecretResult(RunSecretLookup Outcome, string? Kind = null, string? Value = null)
{
    /// <summary>The payload of a hit, with the kind the server said it has.</summary>
    public static RunSecretResult Found(string kind, string value) => new(RunSecretLookup.Found, kind, value);

    /// <summary>
    /// The payload of a hit whose kind was not stated. Only a source written before card F-040 answers this
    /// way, and <c>text</c> is exactly what it means - the one shape a secret could have then.
    /// </summary>
    public static RunSecretResult Found(string value) =>
        new(RunSecretLookup.Found, Zenkins.Shared.Secrets.SecretKinds.Default, value);

    /// <summary>Every miss collapses here.</summary>
    public static RunSecretResult NotFound() => new(RunSecretLookup.NotFound);
}

/// <summary>
/// Where a run's secret values come from (card F-015). In production one HTTP request against the server's
/// run-scoped route; a test substitutes a dictionary. <see cref="FetchAsync"/> may throw for a transport
/// failure - the resolver turns that into a failed step that names the secret.
/// </summary>
public interface IRunSecretSource
{
    /// <summary>Fetches and decrypts one secret for the run this source belongs to.</summary>
    Task<RunSecretResult> FetchAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// One run's in-memory secret cache (card F-015, PRD §3.8).
/// <para>
/// <b>Per run, in memory, never on disk.</b> The cache belongs to a single run's execution and is dropped
/// when that execution ends, so a worker restart re-fetches (there is nothing to reload) and a second run
/// can never read the first run's values even in the same process. Nothing here writes a file, and nothing
/// logs a value.
/// </para>
/// <para>
/// <b>Only hits are cached.</b> A miss is re-fetched if a later step references the same name (a step can
/// reference a name the organization adds mid-run only in theory, but re-asking is cheap and never returns
/// a stale "missing"); a hit is answered from memory, so one step referencing one secret five times costs
/// one request.
/// </para>
/// </summary>
public sealed class RunSecretCache(IRunSecretSource source) : IRunSecretSource
{
    private readonly IRunSecretSource _source = source;
    private readonly object _gate = new();
    private readonly Dictionary<string, RunSecretResult> _values = new(StringComparer.Ordinal);

    /// <summary>How many distinct secrets this run has cached (asserted by the caching test).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _values.Count;
            }
        }
    }

    /// <inheritdoc />
    public async Task<RunSecretResult> FetchAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_gate)
        {
            if (_values.TryGetValue(name, out var cached))
            {
                return cached;
            }
        }

        var result = await _source.FetchAsync(name, cancellationToken);
        if (result.Outcome == RunSecretLookup.Found && result.Value is not null)
        {
            lock (_gate)
            {
                // The kind is cached with the payload: a second reference to the same name must decode it the
                // same way the first one did (card F-040).
                _values[name] = result;
            }
        }

        return result;
    }
}

/// <summary>
/// A source with no secrets at all: the definition-phase-only path and the tests that do not exercise
/// <c>secret()</c>. A reference to it is a step failure that names the name, exactly like an unknown name
/// on the real server.
/// </summary>
public sealed class EmptyRunSecretSource : IRunSecretSource
{
    /// <summary>The shared instance.</summary>
    public static EmptyRunSecretSource Instance { get; } = new();

    /// <inheritdoc />
    public Task<RunSecretResult> FetchAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(RunSecretResult.NotFound());
}
