namespace Zenkins.Worker.Pipelines;

using System.Text;
using Zenkins.Shared.Projects;
using Zenkins.Worker.Git;

/// <summary>
/// Fetches a run's uploaded <c>file</c> parameters and writes them into the run's own directory (card F-041),
/// handing the steps a <b>path</b>.
/// <para>
/// <b>Before any step, and one file at a time.</b> The bytes arrive over the run-scoped, worker-authenticated
/// route (never in the claim: card F-040 settled that the claim carries names), and a parameter that cannot be
/// materialised fails the <b>run</b> with <c>parameter-file-unavailable</c> - before the definition phase and
/// before any step - because a step that read an empty path would fail somewhere far from the cause.
/// </para>
/// <para>
/// <b>Inside the run's tree, owner-only.</b> <c>work/{run-id}/temp/parameters/{NAME}</c>: under <c>temp/</c> so
/// the run's existing cleanup owns it, and created <c>0700</c>/<c>0600</c> on Unix through
/// <see cref="FileStreamOptions.UnixCreateMode"/> so the material is never briefly world-readable. The path is
/// not a secret and is not registered with the redactor; nothing here ever prints the bytes.
/// </para>
/// </summary>
public static class RunParameterFileMaterializer
{
    /// <summary>The subdirectory of the run's <c>temp/</c> that holds parameter uploads.</summary>
    public const string DirectoryName = "parameters";

    /// <summary>
    /// Writes every uploaded file of <paramref name="claim"/> into <paramref name="work"/> and returns the
    /// environment values with those names pointing at their absolute path.
    /// </summary>
    /// <param name="fetch">One name → its bytes, or <see langword="null"/> when the server holds none.</param>
    /// <exception cref="RunParameterFileException">A file could not be fetched, or written.</exception>
    public static async Task<IReadOnlyDictionary<string, string>> MaterializeAsync(
        WorkerClaimResponse claim,
        RunWorkDirectory work,
        Func<string, CancellationToken, Task<byte[]?>> fetch,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(fetch);

        var values = new Dictionary<string, string>(
            claim.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        if (claim.FileParameters is not { Count: > 0 } names)
        {
            return values;
        }

        var directory = Path.Combine(work.TempPath, DirectoryName);
        foreach (var name in names)
        {
            byte[]? bytes;
            try
            {
                bytes = await fetch(name, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception failure)
            {
                throw new RunParameterFileException(
                    $"the file parameter '{name}' could not be fetched: {failure.Message}",
                    failure);
            }

            if (bytes is null)
            {
                throw new RunParameterFileException(
                    $"the file parameter '{name}' is not available for this run.");
            }

            if (bytes.Length > ProjectParameterLimits.MaxFileBytes)
            {
                throw new RunParameterFileException(
                    $"the file parameter '{name}' is {bytes.Length} bytes, over the "
                    + $"{ProjectParameterLimits.MaxFileBytes}-byte limit.");
            }

            Directory.CreateDirectory(directory);
            Restrict(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var path = Path.Combine(directory, name);
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(path, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            // The step reads this instead of the server's display value: an absolute path inside this run's
            // own tree, which is the only place the file exists.
            values[name] = path;
            log($"zenkins: file parameter '{name}' written to {path} ({bytes.Length} bytes).");
        }

        return values;
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
            // A directory we do not own keeps the mode it has; the content is still inside the run's own tree
            // and the run's cleanup removes it.
        }
    }
}

/// <summary>
/// A <c>file</c> parameter that could not be materialised (card F-041): a refused fetch or a failed write. The
/// message names the parameter and never a byte of it, so it is safe in the run log.
/// </summary>
public sealed class RunParameterFileException(string message, Exception? innerException = null)
    : Exception(message, innerException);
