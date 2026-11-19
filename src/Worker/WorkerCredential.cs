namespace Zenkins.Worker;

/// <summary>
/// The worker token this process presents on every Server call, held in one place that can be
/// replaced while the process runs.
/// <para>
/// <b>Why this exists.</b> A worker's identity is a server-side token, not its name: the name picks
/// a row and a directory, the token is what the Server authenticates. A token can stop working for
/// reasons the worker cannot see - the Server's data was replaced or restored, an Owner revoked the
/// credential, the worker re-registered elsewhere - and before this type every loop captured the
/// token as a <see cref="string"/> at construction, so recovering meant restarting the process.
/// </para>
/// <para>
/// <b>Threading.</b> One heartbeat loop writes a renewed token while <see cref="WorkerConfiguration.MaxConcurrency"/>
/// run loops, the probe loop, the validation loop and the log-stream reconnect all read it. Reads
/// and writes are single-reference and go through <see cref="Volatile"/>, so a reader sees either the
/// old or the new token, never a torn value - and a request already in flight simply finishes with
/// the token it started with.
/// </para>
/// </summary>
public sealed class WorkerCredential
{
    private string _current;

    public WorkerCredential(string token)
    {
        Validate(token);
        _current = token;
    }

    /// <summary>The token to present now: the persisted one until the Server accepts a renewal.</summary>
    public string Current => Volatile.Read(ref _current);

    /// <summary>
    /// Replaces the token every loop reads from now on. Called only after the Server issued a new
    /// one and it was persisted, so a crash between the two loses nothing that mattered: the old
    /// token is already revoked server-side, and the next start re-registers from the file.
    /// </summary>
    public void Rotate(string token)
    {
        Validate(token);
        Volatile.Write(ref _current, token);
    }

    /// <summary>
    /// Refuses a value that is not a worker token. A registration token here would be a bug that
    /// burns a single-use credential and 401s forever; failing at the source names it.
    /// </summary>
    private static void Validate(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (!token.StartsWith(WorkerTokenStore.TokenPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"a worker credential must be a {WorkerTokenStore.TokenPrefix}… token.",
                nameof(token));
        }
    }
}
