using System.Security.Cryptography;
using System.Text;

namespace Zenkins.Worker;

/// <summary>
/// Persists the worker token under the user profile (card F-005). The path scheme separates by
/// server host and worker name, so one machine can hold credentials for several servers/workers
/// without them colliding.
/// <para>
/// <b>Permissions.</b> On POSIX the directory chain is created <c>0700</c> and the token file is
/// written <c>0600</c> (mode set on the temporary file <em>before</em> the bytes are written, so no
/// reader ever sees a wider mode). The write is atomic: bytes land in a temp file that is then
/// moved over the target, so a crash mid-write cannot truncate a live credential. On Windows the
/// Unix modes are skipped (best-effort ACL hardening is deliberately deferred; the profile
/// directory is already user-private on Windows).
/// </para>
/// </summary>
public sealed class WorkerTokenStore
{
    /// <summary>The worker token prefix; a file that does not look like one is treated as corrupt.</summary>
    public const string TokenPrefix = "zwrk_";

    private readonly string _storePath;
    private readonly string _directoryPath;

    public WorkerTokenStore(string dataDirectory, Uri serverUrl, string workerName)
    {
        _directoryPath = Path.Combine(
            dataDirectory,
            "worker-tokens",
            SafeName(serverUrl.Authority),
            SafeName(workerName));
        _storePath = Path.Combine(_directoryPath, "worker.token");
    }

    /// <summary>Absolute path of the token file (also the message operators need when it is corrupt).</summary>
    public string StorePath => _storePath;

    /// <summary>The directory holding the token file.</summary>
    public string DirectoryPath => _directoryPath;

    /// <summary>
    /// Loads the persisted worker token, or <c>null</c> when nothing was persisted yet. A file that
    /// exists but does not look like a worker token raises <see cref="WorkerTokenStoreException"/> -
    /// silently ignoring it would loop the worker into 401s with no explanation.
    /// </summary>
    public string? Load()
    {
        if (!File.Exists(_storePath))
        {
            return null;
        }

        var token = File.ReadAllText(_storePath, Encoding.ASCII).Trim();
        if (token.Length == 0)
        {
            throw new WorkerTokenStoreException(
                $"configuration error: the worker token file '{_storePath}' is empty; delete it and re-register.");
        }

        if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            throw new WorkerTokenStoreException(
                $"configuration error: the worker token file '{_storePath}' does not contain a worker token; delete it and re-register.");
        }

        return token;
    }

    /// <summary>Persists the token atomically with restrictive permissions.</summary>
    public void Save(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            throw new WorkerTokenStoreException(
                "internal error: refusing to persist a value that is not a worker token.");
        }

        if (OperatingSystem.IsWindows())
        {
            // The Unix creation mode is unsupported on Windows; the profile directory is already
            // user-private there. Best-effort ACL hardening is deferred (documented in the card).
            Directory.CreateDirectory(_directoryPath);
        }
        else
        {
            Directory.CreateDirectory(
                _directoryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var temporaryPath = _storePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                {
                    // Restrict the mode while the file is empty; no reader can ever see it wider.
                    File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                using var writer = new StreamWriter(stream, Encoding.ASCII);
                writer.Write(token);
            }

            File.Move(temporaryPath, _storePath, overwrite: true);
        }
        finally
        {
            // A failed move must not leak temp files.
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            // Belt and braces: the move preserves the temp file's mode, but if the token file
            // predates this run, make sure it is 0600 now.
            File.SetUnixFileMode(_storePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// A filesystem-safe, collision-free name for one path segment: unsafe characters become
    /// dashes, and a short hash of the raw value keeps lookalike inputs ("a.b" vs "a-b") apart.
    /// The result never forms a "." or ".." segment, so a hostile worker name cannot escape the
    /// store root.
    /// </summary>
    private static string SafeName(string value)
    {
        var builder = new StringBuilder(value.Length + 9);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-');
        }

        // Deterministic disambiguator over the raw value (base64url of a SHA-256 prefix).
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        builder.Append('-');
        builder.Append(Convert.ToBase64String(digest, 0, 6).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        return builder.ToString();
    }
}

/// <summary>A recoverable-but-not-retryable problem with the persisted worker token.</summary>
public sealed class WorkerTokenStoreException(string message) : Exception(message)
{
}
