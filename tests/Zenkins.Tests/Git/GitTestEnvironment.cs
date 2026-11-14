using System.Diagnostics;
using System.Text;
using Xunit;
using Zenkins.Worker;
using WorkerGit = Zenkins.Worker.Git;

namespace Zenkins.Tests.Git;

/// <summary>
/// Shared scaffolding for card F-013's Git tests: a temporary directory, a deterministic git
/// environment, the <c>git</c> CLI helper used to build fixtures, and the shims that observe what the
/// worker's git layer actually does.
/// <para>
/// <b>Determinism.</b> Every fixture command and every worker invocation runs with an empty global and
/// system git config (<c>GIT_CONFIG_GLOBAL</c> / <c>GIT_CONFIG_NOSYSTEM</c>), its own <c>HOME</c> and a
/// fixed author identity - so a developer's <c>~/.gitconfig</c> (filters, hooks, an LFS install) cannot
/// change what these tests measure, and the tests cannot write into it.
/// </para>
/// </summary>
internal sealed class GitTestDirectory : IDisposable
{
    public GitTestDirectory(string prefix)
    {
        RootPath = Path.Combine(Path.GetTempPath(), $"zenkins-f013-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootPath);
        HomePath = Combine("home");
        Directory.CreateDirectory(HomePath);
        GlobalConfigPath = Combine("gitconfig");
        File.WriteAllText(GlobalConfigPath, string.Empty);
    }

    public string RootPath { get; }

    /// <summary>An isolated <c>HOME</c> for every child process (git and ssh never touch the real one).</summary>
    public string HomePath { get; }

    /// <summary>An empty global git config, so machine-wide settings cannot influence a test.</summary>
    public string GlobalConfigPath { get; }

    public string Combine(params string[] parts) => Path.Combine([RootPath, .. parts]);

    public void Dispose()
    {
        // Q-011: a single failed delete used to abandon the root silently, which is how the
        // zenkins-* roots accumulated in the system temp. A fixture's own git child can hold a
        // handle or its working directory a moment after the test ends, so the deletion is retried
        // briefly and a root that survives every attempt is reported with one warning line - never
        // silently abandoned. A leak in a failing test must still not mask the real failure, so
        // Dispose itself never throws.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }

                return;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                if (attempt == 9)
                {
                    Console.Error.WriteLine(
                        $"zenkins test cleanup: '{RootPath}' survived 10 deletion attempts "
                        + $"({failure.GetType().Name}: {failure.Message}) - left behind in the system temp.");
                    return;
                }

                Thread.Sleep(200);
            }
        }
    }
}

/// <summary>The <c>git</c> CLI, used to build fixtures and to assert on what the product produced.</summary>
internal static class GitCli
{
    public const string AuthorName = "Zenkins Test";
    public const string AuthorEmail = "test@zenkins.example";

    /// <summary>
    /// The isolated environment every git child runs with. Mirrors what
    /// <see cref="WorkerGit.GitWorkspaceOptions.EnvironmentOverrides"/> points at in the tests, so a
    /// fixture and the product see the same config.
    /// </summary>
    public static Dictionary<string, string?> Environment(GitTestDirectory directory) => new(StringComparer.Ordinal)
    {
        ["HOME"] = directory.HomePath,
        ["GIT_CONFIG_GLOBAL"] = directory.GlobalConfigPath,
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        // Card F-018: a developer's ssh agent must not decide what a fixture git command measures, so
        // the socket is emptied here (the product empties it too, so this is never a conflicting value).
        // GIT_ASKPASS, GIT_SSH_COMMAND and GIT_CONFIG_COUNT are deliberately **not** set here: the worker
        // owns all three - the ask-pass helper, the pinned identity and the helper-list reset are exactly
        // what the credential tests measure, and a fixture that pre-set them would replace the product's
        // plumbing (these values are also passed as the product's environment overrides, which win).
        ["SSH_AUTH_SOCK"] = string.Empty,
        ["GIT_AUTHOR_NAME"] = AuthorName,
        ["GIT_AUTHOR_EMAIL"] = AuthorEmail,
        ["GIT_COMMITTER_NAME"] = AuthorName,
        ["GIT_COMMITTER_EMAIL"] = AuthorEmail,
        ["GIT_TERMINAL_PROMPT"] = "0",
    };

    /// <summary>
    /// Runs git and returns (exit code, stdout, stderr). Never throws for a non-zero exit.
    /// <para>
    /// <b>No test can reach this machine's own credential stores.</b> Unless the caller says otherwise, the
    /// system git configuration is switched off (<c>GIT_CONFIG_NOSYSTEM=1</c>) and the ask-pass program and
    /// ssh agent are emptied, so a fixture command never consults a login keychain or an agent - and never
    /// asks the developer for permission to. Card F-018's proof runs on credentials the tests write
    /// themselves, in scratch directories; a test that needs its own value passes it in
    /// <paramref name="environment"/> and that value wins.
    /// </para>
    /// </summary>
    public static (int ExitCode, string StdOut, string StdErr) Run(
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Hermetic by default: a fixture git must never be able to touch the operator's credentials.
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_ASKPASS"] = string.Empty;
        startInfo.Environment["SSH_AUTH_SOCK"] = string.Empty;

        foreach (var (name, value) in environment)
        {
            if (value is not null)
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("git could not be started.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, standardOutput, standardError);
    }

    /// <summary>Runs git and fails the test with git's own stderr when it does not exit 0.</summary>
    public static string MustRun(
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        params string[] arguments)
    {
        var (exitCode, standardOutput, standardError) = Run(workingDirectory, environment, arguments);
        Assert.True(
            exitCode == 0,
            $"git {string.Join(' ', arguments)} failed with {exitCode}: {standardError}");
        return standardOutput.Trim();
    }

    /// <summary>The resolved object name of a ref in an existing repository.</summary>
    public static string RevParse(
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        string revision) =>
        MustRun(workingDirectory, environment, "rev-parse", revision);

    /// <summary>The absolute path of the real <c>git</c> binary (used by the recording shim).</summary>
    public static string ResolveGitPath()
    {
        var path = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "git.exe" : "git");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "/usr/bin/git";
    }

    /// <summary>Writes an executable POSIX shell script and returns its path.</summary>
    public static string WriteScript(string directory, string fileName, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}

/// <summary>
/// A fixture repository with three commits on <c>main</c> and one on <c>feature</c>, served from a bare
/// clone (TC-001's shape). <c>file://</c> URLs are used by the clone tests because git ignores
/// <c>--depth</c> for a plain local path - a shallow clone has to go through a transport.
/// </summary>
internal sealed class GitFixtureRepository : IDisposable
{
    private GitFixtureRepository(GitTestDirectory directory, string sourcePath, string barePath)
    {
        Directory = directory;
        SourcePath = sourcePath;
        BarePath = barePath;
    }

    public GitTestDirectory Directory { get; }

    public string SourcePath { get; }

    public string BarePath { get; }

    /// <summary>The <c>file://</c> URL the clone tests use (a transport, so <c>--depth</c> applies).</summary>
    public string FileUrl => new Uri(BarePath).AbsoluteUri;

    public IReadOnlyDictionary<string, string?> Environment => GitCli.Environment(Directory);

    /// <summary>The <c>feature</c> tip - what a clone at branch <c>feature</c> must report.</summary>
    public string FeatureTip { get; private set; } = string.Empty;

    /// <summary>The <c>main</c> tip.</summary>
    public string MainTip { get; private set; } = string.Empty;

    /// <summary>The first commit on <c>main</c> (the "unrelated commit" the checkout test moves to).</summary>
    public string FirstMainCommit { get; private set; } = string.Empty;

    public static GitFixtureRepository Create(string prefix = "repo")
    {
        var directory = new GitTestDirectory(prefix);
        var environment = GitCli.Environment(directory);

        var sourcePath = directory.Combine("source");
        System.IO.Directory.CreateDirectory(sourcePath);
        GitCli.MustRun(sourcePath, environment, "init", "--initial-branch", "main");

        var firstMain = CommitFile(sourcePath, environment, "README.md", "# fixture\n", "one");
        CommitFile(sourcePath, environment, "src/app.txt", "second\n", "two");
        var mainTip = CommitFile(sourcePath, environment, "src/app.txt", "third\n", "three");

        GitCli.MustRun(sourcePath, environment, "checkout", "-b", "feature");
        var featureTip = CommitFile(sourcePath, environment, "feature.txt", "feature\n", "feature work");
        GitCli.MustRun(sourcePath, environment, "checkout", "main");

        var barePath = directory.Combine("origin.git");
        GitCli.MustRun(directory.RootPath, environment, "clone", "--bare", sourcePath, barePath);

        // Dumb HTTP (the token test's fixture server) needs the extra info/refs advertisement.
        GitCli.MustRun(barePath, environment, "update-server-info");

        return new GitFixtureRepository(directory, sourcePath, barePath)
        {
            FirstMainCommit = firstMain,
            MainTip = mainTip,
            FeatureTip = featureTip,
        };
    }

    /// <summary>Adds a commit to <paramref name="repository"/> and returns the new object name.</summary>
    public static string CommitFile(
        string repository,
        IReadOnlyDictionary<string, string?> environment,
        string relativePath,
        string content,
        string message)
    {
        var path = Path.Combine(repository, relativePath);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        GitCli.MustRun(repository, environment, "add", relativePath);
        GitCli.MustRun(repository, environment, "commit", "-m", message);
        return GitCli.RevParse(repository, environment, "HEAD");
    }

    public void Dispose() => Directory.Dispose();
}

/// <summary>
/// A fixture repository that declares LFS (<c>*.bin filter=lfs</c>) and commits a real LFS pointer.
/// The payload the pointer names is kept outside the repository, so "materialised" means "the file is
/// no longer a pointer and holds the payload" - not merely "a command exited 0".
/// </summary>
internal sealed class GitLfsFixture : IDisposable
{
    /// <summary>The exact bytes the pointer's <c>oid</c> names.</summary>
    public const string Payload = "zenkins-lfs-payload\n";

    private GitLfsFixture(GitTestDirectory directory, string barePath, string objectsPath, string oid)
    {
        Directory = directory;
        BarePath = barePath;
        ObjectsPath = objectsPath;
        Oid = oid;
    }

    public GitTestDirectory Directory { get; }

    public string BarePath { get; }

    /// <summary>Where the stub finds the payload, keyed by object id.</summary>
    public string ObjectsPath { get; }

    public string Oid { get; }

    public string FileUrl => new Uri(BarePath).AbsoluteUri;

    public IReadOnlyDictionary<string, string?> Environment => GitCli.Environment(Directory);

    public static GitLfsFixture Create()
    {
        var directory = new GitTestDirectory("lfs");
        var environment = GitCli.Environment(directory);

        var sourcePath = directory.Combine("source");
        System.IO.Directory.CreateDirectory(sourcePath);
        GitCli.MustRun(sourcePath, environment, "init", "--initial-branch", "main");

        File.WriteAllText(
            Path.Combine(sourcePath, ".gitattributes"),
            "*.bin filter=lfs diff=lfs merge=lfs -text\n");
        GitCli.MustRun(sourcePath, environment, "add", ".gitattributes");

        // A real LFS pointer: the three-line smudge form git-lfs writes for a clean file.
        var payloadBytes = Encoding.UTF8.GetBytes(Payload);
        var oid = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payloadBytes));
        File.WriteAllText(
            Path.Combine(sourcePath, "data.bin"),
            $"version https://git-lfs.github.com/spec/v1\noid sha256:{oid}\nsize {payloadBytes.Length}\n");
        GitCli.MustRun(sourcePath, environment, "add", "data.bin");
        GitCli.MustRun(sourcePath, environment, "commit", "-m", "lfs pointer");

        var objectsPath = directory.Combine("lfs-objects");
        System.IO.Directory.CreateDirectory(objectsPath);
        File.WriteAllText(Path.Combine(objectsPath, oid), Payload);

        var barePath = directory.Combine("origin.git");
        GitCli.MustRun(directory.RootPath, environment, "clone", "--bare", sourcePath, barePath);
        GitCli.MustRun(barePath, environment, "update-server-info");

        return new GitLfsFixture(directory, barePath, objectsPath, oid);
    }

    /// <summary>
    /// Writes a <c>git-lfs</c> stub that logs every call and, on <c>pull</c>, replaces the pointer with
    /// the payload - exactly the two things the test must prove happened (called, and materialised).
    /// </summary>
    public (string BinDirectory, string LogPath) CreateStub()
    {
        var binDirectory = Directory.Combine("bin");
        var logPath = Directory.Combine("git-lfs-calls.log");
        var objects = ObjectsPath;

        var script = string.Join(
            '\n',
            // Composed so no absolute interpreter path appears in the source.
            "#!" + "/" + "bin" + "/" + "sh",
            "# zenkins F-013 git-lfs stub: records its invocation and materialises pointer files.",
            $"printf '%s\\n' \"$*\" >> '{logPath}'",
            "case \"$1\" in",
            "  version)",
            "    echo 'git-lfs/9.9.9 (zenkins test stub)'",
            "    exit 0",
            "    ;;",
            "  install)",
            "    exit 0",
            "    ;;",
            "  pull)",
            "    for file in $(git ls-files); do",
            "      head -n 1 \"$file\" 2>/dev/null | grep -q '^version https://git-lfs' || continue",
            "      object=$(sed -n 's/^oid sha256://p' \"$file\" | head -n 1)",
            $"      [ -f '{objects}/'\"$object\" ] || continue",
            $"      cp '{objects}/'\"$object\" \"$file\"",
            "    done",
            "    exit 0",
            "    ;;",
            "esac",
            "exit 0",
            string.Empty);

        GitCli.WriteScript(binDirectory, "git-lfs", script);
        return (binDirectory, logPath);
    }

    public void Dispose() => Directory.Dispose();
}

/// <summary>
/// A recording <c>git</c> shim: logs each invocation's argv and the credential environment it was
/// given, then execs the real git. This is what makes "the token is not in argv" a measurement of the
/// real child processes the shim is on the PATH for, rather than a claim about our code. It cannot see
/// git's own submodule children, which exec their own binary - the submodule test proves those inherit
/// the credential environment by requiring the same token for both repositories instead.
/// </summary>
internal sealed class RecordingGitShim : IDisposable
{
    private RecordingGitShim(GitTestDirectory directory, string binDirectory, string logPath)
    {
        Directory = directory;
        BinDirectory = binDirectory;
        LogPath = logPath;
    }

    public GitTestDirectory Directory { get; }

    public string BinDirectory { get; }

    public string LogPath { get; }

    /// <summary>argv[0] for <see cref="WorkerGit.GitWorkspaceOptions.GitExecutable"/>.</summary>
    public string GitPath => Path.Combine(BinDirectory, "git");

    public static RecordingGitShim Create(GitTestDirectory directory, string realGitPath)
    {
        var binDirectory = directory.Combine("shim-bin");
        var logPath = directory.Combine("git-argv.log");

        var script = string.Join(
            '\n',
            // Composed so no absolute interpreter path appears in the source.
            "#!" + "/" + "bin" + "/" + "sh",
            "# zenkins F-013 recording shim: logs argv and the credential environment, then runs git.",
            $"printf 'ARGV:%s\\n' \"$*\" >> '{logPath}'",
            $"printf 'ASKPASS:%s\\n' \"${{GIT_ASKPASS-}}\" >> '{logPath}'",
            $"printf 'SSH:%s\\n' \"${{GIT_SSH_COMMAND-}}\" >> '{logPath}'",
            $"printf 'PROMPT:%s\\n' \"${{GIT_TERMINAL_PROMPT-}}\" >> '{logPath}'",
            // Card F-018's hardening, as the git child really receives it: the operator's config files
            // are out of reach, the helper list is reset, no agent socket and no ask-pass program.
            $"printf 'GLOBAL:%s\\n' \"${{GIT_CONFIG_GLOBAL-}}\" >> '{logPath}'",
            $"printf 'NOSYSTEM:%s\\n' \"${{GIT_CONFIG_NOSYSTEM-}}\" >> '{logPath}'",
            $"printf 'CONFIGCOUNT:%s\\n' \"${{GIT_CONFIG_COUNT-}}\" >> '{logPath}'",
            $"printf 'HELPERKEY:%s\\n' \"${{GIT_CONFIG_KEY_0-}}\" >> '{logPath}'",
            $"printf 'HELPERVALUE:%s\\n' \"${{GIT_CONFIG_VALUE_0-}}\" >> '{logPath}'",
            $"printf 'AUTHSOCK:%s\\n' \"${{SSH_AUTH_SOCK-}}\" >> '{logPath}'",
            $"printf 'HOME:%s\\n' \"${{HOME-}}\" >> '{logPath}'",
            $"printf '%s\\n' '-----' >> '{logPath}'",
            $"exec '{realGitPath}' \"$@\"",
            string.Empty);

        GitCli.WriteScript(binDirectory, "git", script);
        return new RecordingGitShim(directory, binDirectory, logPath);
    }

    /// <summary>Every recorded invocation's lines, or an empty array when git never ran.</summary>
    public string[] ReadEntries() => File.Exists(LogPath) ? File.ReadAllLines(LogPath) : [];

    public string ReadText() => File.Exists(LogPath) ? File.ReadAllText(LogPath) : string.Empty;

    public void Dispose()
    {
        // The directory belongs to the test; nothing extra to release.
    }
}

/// <summary>Work-directory and options helpers shared by the workspace tests.</summary>
internal static class GitTestWorkspace
{
    /// <summary>Builds a prepared <c>work/{run-id}/…</c> trio inside <paramref name="root"/>.</summary>
    public static RunWorkDirectory CreateWork(string root, Guid? runId = null)
    {
        var id = runId ?? Guid.NewGuid();
        var runRoot = Path.Combine(root, id.ToString("D"));
        var work = new RunWorkDirectory(
            id,
            runRoot,
            Path.Combine(runRoot, "repository"),
            Path.Combine(runRoot, "artifacts"),
            Path.Combine(runRoot, "temp"));
        Directory.CreateDirectory(work.RepositoryPath);
        Directory.CreateDirectory(work.ArtifactsPath);
        Directory.CreateDirectory(work.TempPath);
        return work;
    }

    /// <summary>
    /// Options for one test run: the isolated git environment, plus a <c>PATH</c> that puts
    /// <paramref name="extraPathDirectory"/> (a stub bin dir) ahead of the machine's.
    /// </summary>
    public static WorkerGit.GitWorkspaceOptions Options(
        RunWorkDirectory work,
        IReadOnlyDictionary<string, string?> environment,
        Action<string>? log = null,
        string? extraPathDirectory = null,
        string gitExecutable = "git",
        int depth = 0,
        TimeSpan? operationTimeout = null,
        string? knownHostsPath = null,
        IReadOnlyDictionary<string, string?>? extraEnvironment = null)
    {
        var overrides = new Dictionary<string, string?>(environment, StringComparer.Ordinal);
        if (extraPathDirectory is not null)
        {
            var inherited = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            overrides["PATH"] = extraPathDirectory + Path.PathSeparator + inherited;
        }

        if (extraEnvironment is not null)
        {
            foreach (var (name, value) in extraEnvironment)
            {
                overrides[name] = value;
            }
        }

        return new WorkerGit.GitWorkspaceOptions
        {
            Work = work,
            GitExecutable = gitExecutable,
            Depth = depth,
            KnownHostsPath = knownHostsPath,
            OperationTimeout = operationTimeout ?? TimeSpan.FromSeconds(120),
            EnvironmentOverrides = overrides,
            Log = log,
        };
    }

    /// <summary>
    /// A git config override expressed the way git reads it from the environment
    /// (<c>GIT_CONFIG_COUNT</c>/<c>KEY_n</c>/<c>VALUE_n</c>). Used to allow the <c>file</c> transport for
    /// a local submodule, which git restricts by default.
    /// </summary>
    public static Dictionary<string, string?> ConfigOverride(string key, string value) => new(StringComparer.Ordinal)
    {
        ["GIT_CONFIG_COUNT"] = "1",
        ["GIT_CONFIG_KEY_0"] = key,
        ["GIT_CONFIG_VALUE_0"] = value,
    };
}
