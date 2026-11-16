using Xunit;

namespace Zenkins.Tests.Git;

/// <summary>
/// Locates and parameterises the compiled recording shim (card B-004): a console TestAsset built with
/// the tests, started by the worker exactly like the POSIX <c>#!/bin/sh</c> shims are.
/// <para>
/// The POSIX path keeps using the shell scripts (<see cref="RecordingGitShim"/> writes one there), so
/// this helper exists for two callers: the Windows branch of the shim factories, and
/// <see cref="RecordingShimAssetTests"/>, which runs the binary on every OS to prove its log lines and
/// its exec behaviour rather than only compiling it.
/// </para>
/// <para>
/// The asset reads its configuration from these variables (mirrored as constants in
/// <c>TestAssets/RecordingShim/Program.cs</c>; the asset is a separate assembly, so the two lists are
/// kept in step by hand and exercised by <see cref="RecordingShimAssetTests"/>).
/// </para>
/// </summary>
internal static class RecordingShimAsset
{
    /// <summary>Selects the asset's behaviour: <see cref="GitRole"/> or <see cref="HangRole"/>.</summary>
    public const string RoleVariable = "ZENKINS_RECORDING_SHIM_ROLE";

    /// <summary>Where the recording git shim appends its twelve lines per invocation.</summary>
    public const string LogVariable = "ZENKINS_RECORDING_SHIM_LOG";

    /// <summary>The real <c>git</c> the recording shim execs.</summary>
    public const string RealVariable = "ZENKINS_RECORDING_SHIM_REAL";

    /// <summary>Where the hanging stub appends its own pid and its sleeping child's pid.</summary>
    public const string PidFileVariable = "ZENKINS_RECORDING_SHIM_PID_FILE";

    /// <summary>The sleeping child's life span, internal to the hanging stub.</summary>
    public const string SleepSecondsVariable = "ZENKINS_RECORDING_SHIM_SLEEP_SECONDS";

    public const string GitRole = "git";
    public const string HangRole = "hang";

    /// <summary>
    /// The built asset, as an executable this OS can start: <c>RecordingShim.exe</c> on Windows (the
    /// apphost, which is what <c>Process.Start</c> needs there), <c>RecordingShim</c> on POSIX. The
    /// newest build output wins, the same assumption <c>ArgvEchoBinary</c> makes: <c>dotnet test</c>
    /// just rebuilt the asset in the running configuration.
    /// </summary>
    public static string ExecutablePath { get; } = Locate();

    /// <summary>The environment a recording git shim needs (log path, real git, role).</summary>
    public static Dictionary<string, string?> GitShimEnvironment(string logPath, string realGitPath) =>
        new(StringComparer.Ordinal)
        {
            [RoleVariable] = GitRole,
            [LogVariable] = logPath,
            [RealVariable] = realGitPath,
        };

    /// <summary>The environment the hanging-git stub needs (role and pid file).</summary>
    public static Dictionary<string, string?> HangingGitEnvironment(string pidFile) =>
        new(StringComparer.Ordinal)
        {
            [RoleVariable] = HangRole,
            [PidFileVariable] = pidFile,
        };

    private static string Locate()
    {
        var repositoryRoot = FindRepositoryRoot();
        foreach (var part in new[] { "tests", "Zenkins.Tests", "TestAssets", "RecordingShim" })
        {
            repositoryRoot = Path.Combine(repositoryRoot, part);
        }

        var binDirectory = Path.Combine(repositoryRoot, "bin");
        var candidates = Directory.EnumerateFiles(binDirectory, "RecordingShim.dll", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}net10.0{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToArray();

        Assert.True(
            candidates.Length > 0,
            $"No built 'RecordingShim.dll' found under '{binDirectory}'. Building the test project builds it; check the ProjectReference.");

        var buildDirectory = candidates[^1].DirectoryName!;
        var launcher = Path.Combine(buildDirectory, OperatingSystem.IsWindows() ? "RecordingShim.exe" : "RecordingShim");
        Assert.True(
            File.Exists(launcher),
            $"'{launcher}' is missing: the recording shim is started as a native process, so its apphost must be built beside the DLL.");
        return launcher;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Zenkins.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the Zenkins repository root from the test output.");
    }
}
