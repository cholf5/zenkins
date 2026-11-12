using System.Reflection;
using Xunit;

namespace Zenkins.Tests;

/// <summary>
/// Secondary AC7 check: <c>Zenkins.Shared</c> is consumed by the console Worker, so it must
/// not expose ASP.NET Core types. This only sees assemblies reached through used types, so
/// it cannot catch a <c>FrameworkReference</c>; the load-bearing guards are the MSBuild
/// check in <c>Directory.Build.targets</c> and <see cref="WorkerRuntimeConfigTests"/>.
/// </summary>
public sealed class SharedAssemblyTests
{
    [Fact]
    public void Shared_assembly_does_not_reference_aspnetcore()
    {
        var shared = Assembly.Load("Zenkins.Shared");

        var aspNetCoreReferences = shared
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .OfType<string>()
            .Where(name => name.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(aspNetCoreReferences);
    }
}
