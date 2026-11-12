using System.Reflection;
using Xunit;

namespace Zenkins.Tests;

/// <summary>
/// Guards the project-layout rules from card I-001: <c>Zenkins.Shared</c> is consumed by
/// the console Worker, so it must never drag ASP.NET Core into that dependency graph.
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
