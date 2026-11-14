using System.Diagnostics;
using System.Reflection;
using Xunit;
using Zenkins.Shared;
using Zenkins.Tests.Milestone;

namespace Zenkins.Tests;

/// <summary>
/// Card P-001's version stamp: one <c>Version</c> property in <c>Directory.Build.props</c> is the
/// single source, the SDK writes it into every assembly's <c>AssemblyInformationalVersion</c>
/// (appending the source revision, which <see cref="ProductVersion"/> strips), and both binaries
/// surface it through their <c>--version</c> flag. This fast-suite fact runs the built
/// framework-dependent Server and Worker DLLs through <c>dotnet exec --version</c> - the harnesses'
/// own binary-selection rule via <see cref="MilestoneHost.FindBuiltBinary"/> - and asserts one stamp
/// end to end: the test assembly's, the Server assembly's, the Worker assembly's (read in-process
/// from the attribute) and both binaries' printed lines. The AOT side of the same claim is the
/// PublishAot fixture's <c>--version</c> probe on the native Worker executable
/// (<c>PublishedAotSmokeTests.PublishedAotArtifacts</c>).
/// </summary>
public sealed class ProductVersionTests
{
    [Fact]
    public void The_server_and_worker_binaries_report_one_build_stamp_version()
    {
        // The stamp every project in the solution inherits from Directory.Build.props - the Tests
        // assembly included - plus the Server's and Worker's own, read from the attribute.
        var stamped = ProductVersion.From(typeof(ProductVersionTests).Assembly);
        Assert.NotEqual("unknown", stamped);
        Assert.Equal(stamped, ProductVersion.From(Assembly.Load("Zenkins.Server")));
        Assert.Equal(stamped, ProductVersion.From(Assembly.Load("Zenkins.Worker")));

        // The operator's surface: each real binary prints "Zenkins <Role> <stamp>" and exits 0.
        var repositoryRoot = MilestoneHost.FindRepositoryRoot();
        Assert.Equal(
            "Zenkins Server " + stamped,
            RunVersionFlag(MilestoneHost.FindBuiltBinary(
                repositoryRoot, Path.Combine("src", "Server", "bin"), "Zenkins.Server.dll")));
        Assert.Equal(
            "Zenkins Worker " + stamped,
            RunVersionFlag(MilestoneHost.FindBuiltBinary(
                repositoryRoot, Path.Combine("src", "Worker", "bin"), "Zenkins.Worker.dll")));
    }

    /// <summary>
    /// Runs <c>dotnet exec &lt;appDll&gt; --version</c> and returns the trimmed stdout. The flag is
    /// answered before the hosts build anything, so this needs no port, no database and no network.
    /// </summary>
    private static string RunVersionFlag(string appDll)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = MilestoneHost.ResolveDotnetPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(appDll);
        startInfo.ArgumentList.Add("--version");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"could not start '{startInfo.FileName}' for the --version probe.");

        // Read both pipes before waiting for exit, so a chatty child cannot deadlock the wait.
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), $"the --version probe for '{appDll}' did not exit within 60 s.");
        Assert.True(
            process.ExitCode == 0,
            $"the --version probe for '{appDll}' exited {process.ExitCode}: {stderr.Trim()}");
        return stdout.Trim();
    }
}
