using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Zenkins.Tests.Auth;

/// <summary>Pins the deployment-facing configuration the auth cookie depends on.</summary>
[Trait("Category", "Auth")]
public sealed class HostConfigurationTests
{
    [Fact]
    public void Forwarded_proxy_allowlist_is_bound_from_configuration()
    {
        using var host = new AuthTestHost(configureAppConfiguration: configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ForwardedHeaders:KnownProxies:0"] = "203.0.113.7",
                ["ForwardedHeaders:KnownNetworks:0"] = "198.51.100.0/24",
            }));

        var options = host.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        // Without this binding a deployment behind a non-loopback proxy could not make
        // X-Forwarded-Proto trusted, so the comment in Program.cs would be a lie.
        Assert.Contains(IPAddress.Parse("203.0.113.7"), options.KnownProxies);
        Assert.Contains(
            options.KnownIPNetworks,
            network => network.BaseAddress.Equals(IPAddress.Parse("198.51.100.0"))
                && network.PrefixLength == 24);
    }
}
