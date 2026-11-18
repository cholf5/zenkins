namespace Zenkins.Worker.Pipelines;

using System.Text;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Secrets;

/// <summary>
/// One run's materialisation directory for <b>file</b> secrets (card F-040).
/// <para>
/// <b>Inside the run's own tree, never a shared location.</b> The directory is
/// <c>work/{run-id}/temp/secrets</c>, so it lives under the run's own temp tree: a busy worker's runs never
/// see each other's files, and the run's existing temp cleanup (<c>WorkDirectoryService.CleanupTemp</c>)
/// removes them with the rest of <c>temp/</c>. Nothing here decides <em>when</em> cleanup runs - it only
/// guarantees the files are somewhere that cleanup already owns.
/// </para>
/// <para>
/// <b>Owner-only on Unix.</b> The directory is created <c>0700</c> and each file <c>0600</c> through
/// <see cref="FileStreamOptions.UnixCreateMode"/>, which sets the mode as the file is created rather than after
/// it exists (a chmod-after-write leaves a window in which the material is world-readable). On Windows the
/// per-run directory already carries the worker account's ACL, and the mode fields are ignored.
/// </para>
/// <para>
/// <b>The path is not secret.</b> The name is a validated environment-variable-shaped secret name, so the path
/// is composed of a GUID directory and that name - no secret byte is in it, which is why the redactor does not
/// register it and a log may print it.
/// </para>
/// </summary>
public sealed class RunSecretFiles
{
    /// <summary>The subdirectory of the run's <c>temp/</c> that holds the materialised files.</summary>
    public const string DirectoryName = "secrets";

    private readonly string _directory;

    /// <param name="directory">The absolute directory to materialise into (the run's <c>temp/secrets</c>).</param>
    public RunSecretFiles(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    /// <summary>Where this run's file secrets live (asserted by the tests; never a secret path).</summary>
    public string DirectoryPath => _directory;

    /// <summary>
    /// Writes one file secret and returns its path. Idempotent per name: a second reference to the same secret
    /// rewrites the same file rather than accumulating copies.
    /// </summary>
    /// <exception cref="RunSecretFileException">
    /// The name is not a legal secret name, or the content is over <see cref="SecretFileLimits.MaxBytes"/>. The
    /// message names the secret and the rule; it never carries the material.
    /// </exception>
    public string Materialize(string name, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);

        if (!StepReference.IsValidName(name))
        {
            throw new RunSecretFileException(
                $"'{name}' is not a legal secret name, so no file can be written for it.");
        }

        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length > SecretFileLimits.MaxBytes)
        {
            throw new RunSecretFileException(
                $"the file secret '{name}' is {bytes.Length} bytes, which is over the "
                + $"{SecretFileLimits.MaxBytes}-byte limit; nothing was written.");
        }

        Directory.CreateDirectory(_directory);
        Restrict(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var path = Path.Combine(_directory, name);
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows())
        {
            // Set at creation, not after: the file never exists with a wider mode than this.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(path, options))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        return path;
    }

    private static void Restrict(string directory, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(directory, mode);
        }
        catch (UnauthorizedAccessException)
        {
            // A directory we do not own (an exotic mount) keeps the mode it has. Not fatal: the content is
            // still inside the run's own tree, and the run's cleanup removes it.
        }
    }
}

/// <summary>
/// A file secret that could not be materialised (card F-040): an illegal name or over the size bound. The
/// message names the secret and the rule and never the material, so it is safe to put in the run log.
/// </summary>
public sealed class RunSecretFileException(string message) : Exception(message);
