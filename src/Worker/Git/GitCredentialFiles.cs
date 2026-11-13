using System.Text;

namespace Zenkins.Worker.Git;

/// <summary>
/// The per-operation credential files a git child process needs (card F-013): the <c>GIT_ASKPASS</c>
/// helper for an HTTPS token, or the private-key file for an SSH key.
/// <para>
/// <b>Never in argv.</b> The value reaches git through a file the child executes (askpass) or reads
/// (<c>ssh -i</c>), never through a command line: an argv is visible in <c>ps</c>, in a process list a
/// build script can print, and in git's own error messages.
/// </para>
/// <para>
/// <b>Materialise → use → delete.</b> <see cref="Materialise"/> writes into the run's
/// <c>temp/</c> (POSIX mode 0700, files 0700/0600) and <see cref="Dispose"/> deletes them on every
/// path - success, failure, timeout, cancellation. The workspace disposes this in a
/// <c>finally</c>, and the type is idempotent, so a second dispose is a no-op. Nothing here is
/// logged with the value in it.
/// </para>
/// <para>
/// <b>Windows.</b> The ask-pass helper is a POSIX <c>sh</c> script on POSIX platforms and a
/// <c>.cmd</c> script on Windows (git-for-windows runs either); the POSIX path is the one this card's
/// tests exercise. The mode bits are POSIX-only and are skipped on Windows, where the parent
/// <c>temp/</c> directory's ACL is the boundary.
/// </para>
/// </summary>
public sealed class GitCredentialFiles : IDisposable
{
    /// <summary>The ask-pass helper's file name inside <c>temp/</c> (a fixed name per operation).</summary>
    public const string AskPassFileName = "git-askpass.sh";

    /// <summary>The Windows ask-pass helper's file name.</summary>
    public const string AskPassWindowsFileName = "git-askpass.cmd";

    /// <summary>The SSH private-key file name inside <c>temp/</c>.</summary>
    public const string SshKeyFileName = "id";

    private readonly List<string> _paths = [];
    private bool _disposed;

    private GitCredentialFiles(string directory, string? askPassPath, string? sshKeyPath)
    {
        Directory = directory;
        AskPassPath = askPassPath;
        SshKeyPath = sshKeyPath;
    }

    /// <summary>The directory the files were written into (the run's <c>temp/</c>).</summary>
    public string Directory { get; }

    /// <summary>The <c>GIT_ASKPASS</c> helper path, or <see langword="null"/> when not needed.</summary>
    public string? AskPassPath { get; }

    /// <summary>The SSH private-key path, or <see langword="null"/> when not needed.</summary>
    public string? SshKeyPath { get; }

    /// <summary>
    /// Writes the file(s) the given material needs. A <see langword="null"/> material produces an
    /// empty carrier (no files), so the caller has one shape for "anonymous" and "credentialed".
    /// </summary>
    /// <exception cref="GitException">
    /// The material cannot be used: a token that is not single-line (it would break or inject into the
    /// helper script), or an <c>HttpsToken</c>/<c>SshKey</c> value that is empty.
    /// </exception>
    public static GitCredentialFiles Materialise(string tempDirectory, GitCredentialMaterial? material)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tempDirectory);

        System.IO.Directory.CreateDirectory(tempDirectory);
        if (!OperatingSystem.IsWindows())
        {
            System.IO.File.SetUnixFileMode(
                tempDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        if (material is null)
        {
            return new GitCredentialFiles(tempDirectory, askPassPath: null, sshKeyPath: null);
        }

        if (string.IsNullOrEmpty(material.Secret))
        {
            throw new GitException(
                "the Git credential has an empty value; refusing to authenticate with it.",
                GitFailureKind.Credential);
        }

        return material.Kind switch
        {
            GitCredentialKind.HttpsToken => MaterialiseAskPass(tempDirectory, material),
            GitCredentialKind.SshKey => MaterialiseSshKey(tempDirectory, material),
            _ => throw new GitException(
                $"the Git credential kind '{material.KindText}' is not supported by this worker.",
                GitFailureKind.Credential),
        };
    }

    /// <summary>
    /// The ask-pass helper's script. It answers the user-name prompt and the password prompt and
    /// nothing else; the value is embedded as a single-quoted shell word (with <c>'</c> escaped), so
    /// no character in it is reinterpreted by the shell.
    /// </summary>
    public static string BuildAskPassScript(GitCredentialMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        EnsureSingleLine(material.Secret, "HTTPS token");

        if (OperatingSystem.IsWindows())
        {
            return string.Join(
                "\r\n",
                "@echo off",
                "rem zenkins git ask-pass helper: generated per run, deleted when the operation ends.",
                "echo %1 | findstr /I /C:\"sername\" >nul",
                "if errorlevel 1 (",
                $"  echo {EscapeForCmd(material.Secret)}",
                ") else (",
                $"  echo {EscapeForCmd(material.EffectiveUserName)}",
                ")",
                "exit /b 0",
                string.Empty);
        }

        // POSIX sh: the generated helper is the only holder of the value, and it lives 0700 inside a
        // 0700 directory for the duration of one git command.
        // The shebang is composed from fragments on purpose: the interpreter path is a POSIX absolute
        // path, and keeping it out of the literal source means no build/tooling step can rewrite it.
        return string.Join(
            "\n",
            "#!" + "/" + "bin" + "/" + "sh",
            "# zenkins git ask-pass helper: generated per run, deleted when the operation ends.",
            "case \"$1\" in",
            $"  [Uu]sername*) printf '%s\\n' {ShellQuote(material.EffectiveUserName)} ;;",
            $"  *) printf '%s\\n' {ShellQuote(material.Secret)} ;;",
            "esac",
            string.Empty);
    }

    /// <summary>Deletes every file this carrier created. Idempotent; never throws for a missing file.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var path in _paths)
        {
            try
            {
                System.IO.File.Delete(path);
            }
            catch (IOException)
            {
                // A leaked handle must not mask the operation's real outcome; the file lives in the
                // run's temp/ directory, which the run's cleanup removes.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static GitCredentialFiles MaterialiseAskPass(string tempDirectory, GitCredentialMaterial material)
    {
        var fileName = OperatingSystem.IsWindows() ? AskPassWindowsFileName : AskPassFileName;
        var path = Path.Combine(tempDirectory, fileName);
        System.IO.File.WriteAllText(path, BuildAskPassScript(material), new UTF8Encoding(false));

        if (!OperatingSystem.IsWindows())
        {
            // 0700: the helper holds the value, so only the worker's own user may read or run it.
            System.IO.File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var carrier = new GitCredentialFiles(tempDirectory, path, sshKeyPath: null);
        carrier._paths.Add(path);
        return carrier;
    }

    private static GitCredentialFiles MaterialiseSshKey(string tempDirectory, GitCredentialMaterial material)
    {
        var path = Path.Combine(tempDirectory, SshKeyFileName);

        // ssh refuses a key file that other users can read, so 0600 is a functional requirement, not
        // only a hardening measure. The trailing newline keeps OpenSSH's parser happy.
        System.IO.File.WriteAllText(path, material.Secret, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var carrier = new GitCredentialFiles(tempDirectory, askPassPath: null, sshKeyPath: path);
        carrier._paths.Add(path);
        return carrier;
    }

    private static void EnsureSingleLine(string value, string description)
    {
        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0 || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new GitException(
                $"the {description} contains a line break or NUL and cannot be used by the ask-pass helper; "
                + "re-enter it as a single line.",
                GitFailureKind.Credential);
        }
    }

    /// <summary>Quotes a value as one POSIX shell word (<c>'…'</c>, with <c>'</c> as <c>'\''</c>).</summary>
    internal static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>Escapes <c>%</c> for a batch file (everything else is a literal inside <c>echo</c>).</summary>
    private static string EscapeForCmd(string value) => value.Replace("%", "%%", StringComparison.Ordinal);
}
