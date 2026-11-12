using System.Text.Json;
using Xunit;

namespace Zenkins.Tests;

/// <summary>
/// AC7 guard for the failure mode the assembly-reference test cannot see: adding a
/// reference to ASP.NET Core in a shared library leaves assembly references and package
/// graphs of the Worker looking clean, but propagates into the <c>runtimeconfig.json</c>
/// of every app that consumes it — here, the Worker.
/// </summary>
public sealed class WorkerRuntimeConfigTests
{
    private const string WorkerRuntimeConfigFileName = "Zenkins.Worker.runtimeconfig.json";

    [Fact]
    public void Worker_runtimeconfig_does_not_include_aspnetcore_frameworks()
    {
        // MSBuild copies the Worker's apphost, deps and runtimeconfig next to the test
        // assembly (full ProjectReference in Zenkins.Tests.csproj), so this
        // never has to guess a configuration, target framework or output layout.
        var runtimeConfigPath = Path.Combine(AppContext.BaseDirectory, WorkerRuntimeConfigFileName);

        if (!File.Exists(runtimeConfigPath))
        {
            Assert.Fail(
                $"{runtimeConfigPath} was not found, so the only guard for the transitive ASP.NET Core "
                + "reference path cannot run. This test fails closed on purpose. Zenkins.Tests.csproj "
                + "references ../../src/Worker/Zenkins.Worker.csproj (full reference, no "
                + "ReferenceOutputAssembly override) so the Worker's apphost, deps and runtimeconfig.json "
                + "are copied into this output folder.");
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
}
