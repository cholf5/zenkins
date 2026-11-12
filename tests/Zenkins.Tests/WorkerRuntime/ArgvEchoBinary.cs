using System.Diagnostics;
using Xunit;
using Zenkins.Worker;

namespace Zenkins.Tests.WorkerRuntime;

/// <summary>
/// Locates the ArgvEcho test-asset binary (card F-007): a ProjectReference with
/// <c>ReferenceOutputAssembly=false</c> makes building the tests build the asset, and the tests
/// run it through <c>dotnet exec</c> — which also proves the exact-argv path end to end, because
/// the child is a real process started with the real dotnet host.
/// <para>
/// The newest build output across Debug/Release wins: <c>dotnet test</c> just rebuilt the asset in
/// the running configuration, so the newest DLL is the matching one (same assumption the E2E
/// helpers make, widened to both configurations because unit tests run under either).
/// </para>
/// </summary>
internal static class ArgvEchoBinary
{
    public static readonly string DotNetPath = ResolveDotnetPath();

    public static string DllPath { get; } = Locate();

    /// <summary>A complete argv to run ArgvEcho with <paramref name="modeAndArgs"/> (not including the dotnet host).</summary>
    public static string[] Command(params string[] modeAndArgs)
    {
        var command = new string[2 + modeAndArgs.Length];
        command[0] = "exec";
        command[1] = DllPath;
        modeAndArgs.CopyTo(command, 2);
        return command;
    }

    private static string Locate()
    {
        var assetDirectory = FindRepositoryRoot();
        foreach (var part in new[] { "tests", "Zenkins.Tests", "TestAssets", "ArgvEcho" })
        {
            assetDirectory = Path.Combine(assetDirectory, part);
        }

        var binDirectory = Path.Combine(assetDirectory, "bin");
        var candidates = Directory.EnumerateFiles(binDirectory, "ArgvEcho.dll", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}net10.0{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToArray();

        Assert.True(
            candidates.Length > 0,
            $"No built 'ArgvEcho.dll' found under '{binDirectory}'. Building the test project builds it; check the ProjectReference.");
        return candidates[^1].FullName;
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

    /// <summary>
    /// The dotnet host that can <c>exec</c> an app DLL (same resolution as the E2E helpers:
    /// <see cref="Environment.ProcessPath"/> under xunit v3 is the test host's apphost, which does
    /// not understand <c>exec</c>, so fall back to DOTNET_ROOT and the shared runtime location).
    /// </summary>
    private static string ResolveDotnetPath()
    {
        var processPath = Environment.ProcessPath;
        if (processPath is not null
            && Path.GetFileName(processPath).StartsWith("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (dotnetRoot is not null && File.Exists(dotnetRoot))
        {
            return dotnetRoot;
        }

        try
        {
            // <root>/shared/Microsoft.NETCore.App/<version> → the host sits at <root>/dotnet.
            var runtimeDirectory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            var host = Path.GetFullPath(
                Path.Combine(runtimeDirectory, "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            if (File.Exists(host))
            {
                return host;
            }
        }
        catch (PlatformNotSupportedException)
        {
        }

        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }
}

/// <summary>Shared plumbing for the WorkerRuntime process tests: an output-line collector.</summary>
internal sealed class OutputCollector
{
    private readonly object _lock = new();
    private readonly List<ProcessOutputLine> _lines = [];

    public Action<ProcessOutputLine> Handler => line =>
    {
        lock (_lock)
        {
            _lines.Add(line);
        }
    };

    public ProcessOutputLine[] Snapshot()
    {
        lock (_lock)
        {
            return [.. _lines];
        }
    }

    public string[] StdOutLines() =>
        Snapshot().Where(line => line.Stream == ProcessOutputStream.StdOut).Select(line => line.Line).ToArray();

    public string[] StdErrorLines() =>
        Snapshot().Where(line => line.Stream == ProcessOutputStream.StdError).Select(line => line.Line).ToArray();

    public Task<ProcessOutputLine> WaitForFirstLineAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return WaitForAsync(
            () => Snapshot().FirstOrDefault(),
            timeout,
            cancellationToken,
            "no output line ever arrived");
    }

    private static async Task<T> WaitForAsync<T>(
        Func<T?> probe,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string timeoutMessage)
        where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } match)
            {
                return match;
            }

            await Task.Delay(25, cancellationToken);
        }

        throw new TimeoutException(timeoutMessage);
    }
}
