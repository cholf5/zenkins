using System.Text.Json;
using Xunit;

namespace Zenkins.Tests;

/// <summary>
/// AC7 guard for the failure mode the assembly-reference test cannot see: adding
/// <c>&lt;FrameworkReference Include="Microsoft.AspNetCore.App" /&gt;</c> to a shared library
/// leaves assembly references and package graphs untouched, but propagates into the
/// <c>runtimeconfig.json</c> of every app that consumes it — here, the Worker.
/// </summary>
public sealed class WorkerRuntimeConfigTests
{
    [Fact]
    public void Worker_runtimeconfig_does_not_include_aspnetcore_frameworks()
    {
        var runtimeConfigPath = FindWorkerRuntimeConfig();

        if (runtimeConfigPath is null)
        {
            Assert.Skip(
                "Zenkins.Worker has no runtimeconfig.json for this configuration and target framework yet; "
                + "run `dotnet build` before `dotnet test` to exercise this guard.");
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(runtimeConfigPath));

        var frameworks = ReadFrameworkNames(document.RootElement, "framework")
            .Concat(ReadFrameworkNames(document.RootElement, "frameworks"))
            .Concat(ReadFrameworkNames(document.RootElement, "includedFrameworks"))
            .ToArray();

        var aspNetCoreFrameworks = frameworks
            .Where(name => name.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(aspNetCoreFrameworks);
    }

    private static IEnumerable<string> ReadFrameworkNames(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty("runtimeOptions", out var runtimeOptions)
            || !runtimeOptions.TryGetProperty(propertyName, out var frameworks))
        {
            return [];
        }

        // A single framework is emitted as "framework": { ... }, several as
        // "frameworks": [ { ... }, ... ], and a self-contained publish as "includedFrameworks".
        return frameworks.ValueKind switch
        {
            JsonValueKind.Object => FrameworkName(frameworks) is { } single ? [single] : [],
            JsonValueKind.Array => frameworks.EnumerateArray().Select(FrameworkName).OfType<string>().ToArray(),
            _ => [],
        };
    }

    private static string? FrameworkName(JsonElement framework) =>
        framework.TryGetProperty("name", out var name) ? name.GetString() : null;

    /// <summary>
    /// Locates the Worker's runtimeconfig.json relative to the test assembly output
    /// (<c>tests/Zenkins.Tests/bin/&lt;Configuration&gt;/&lt;tfm&gt;</c>) and the repository root.
    /// </summary>
    private static string? FindWorkerRuntimeConfig()
    {
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var targetFramework = outputDirectory.Name;
        var configuration = outputDirectory.Parent?.Name;

        if (configuration is null)
        {
            return null;
        }

        var repositoryRoot = FindRepositoryRoot(outputDirectory);
        if (repositoryRoot is null)
        {
            return null;
        }

        var candidate = Path.Combine(
            repositoryRoot.FullName,
            "src", "Worker", "bin", configuration, targetFramework, "Zenkins.Worker.runtimeconfig.json");

        return File.Exists(candidate) ? candidate : null;
    }

    private static DirectoryInfo? FindRepositoryRoot(DirectoryInfo start)
    {
        for (var directory = start; directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Zenkins.slnx")))
            {
                return directory;
            }
        }

        return null;
    }
}
