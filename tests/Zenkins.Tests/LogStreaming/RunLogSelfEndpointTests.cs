using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Zenkins.Server.LogStreaming;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.LogStreaming;

/// <summary>
/// Bug B-017: the run console's log connection dials the address this server is listening on, not the
/// browser's URL. Behind a forwarded port or an authenticating reverse proxy the browser's URL answers
/// 401 to a plain request from the server itself, and the panel reported the log stream as unreachable
/// while the hub was healthy. These tests pin the choice (pure) and prove the chosen address really
/// reaches the listener (a real Kestrel host, wildcard bind).
/// </summary>
[Trait("Category", "LogStreaming")]
public sealed class RunLogSelfEndpointTests
{
    [Fact]
    public void A_wildcard_bind_normalises_to_loopback_on_the_same_port()
    {
        // 0.0.0.0 accept loopback too; naming it keeps the connection off the network stack.
        Assert.Equal(
            new Uri("http://127.0.0.1:5000/"),
            RunLogSelfEndpoint.Choose(["http://0.0.0.0:5000"]));

        Assert.Equal(
            new Uri("http://127.0.0.1:15004/"),
            RunLogSelfEndpoint.Choose(["http://[::]:15004"]));
    }

    [Fact]
    public void Loopback_wins_over_a_public_or_proxied_address()
    {
        Assert.Equal(
            new Uri("http://127.0.0.1:5000/"),
            RunLogSelfEndpoint.Choose([
                "https://ci.example.test",
                "http://10.0.0.5:15004",
                "http://127.0.0.1:5000",
            ]));
    }

    [Fact]
    public void Plain_http_loopback_wins_over_https_loopback()
    {
        // A loopback TLS hop would have to trust this host's own certificate for no benefit.
        Assert.Equal(
            new Uri("http://127.0.0.1:5067/"),
            RunLogSelfEndpoint.Choose(["https://localhost:7015", "http://127.0.0.1:5067"]));
    }

    [Fact]
    public void An_ipv6_loopback_endpoint_is_kept_as_it_is()
    {
        Assert.Equal(
            new Uri("http://[::1]:5000/"),
            RunLogSelfEndpoint.Choose(["http://[::1]:5000"]));
    }

    [Fact]
    public void A_concrete_address_is_still_this_machine()
    {
        // RFC 5737 documentation address: the shape of "bound to one interface" without a real host.
        Assert.Equal(
            new Uri("http://192.0.2.10:5000/"),
            RunLogSelfEndpoint.Choose(["http://192.0.2.10:5000"]));
    }

    [Fact]
    public void Unusable_entries_are_ignored_rather_than_reported_as_a_destination()
    {
        Assert.Null(RunLogSelfEndpoint.Choose([
            null,
            "",
            "   ",
            "npipe://./pipe/zenkins",
            "unix:/tmp/zenkins.sock",
            "http://127.0.0.1:0",
        ]));

        // One real endpoint among junk still resolves.
        Assert.Equal(
            new Uri("http://127.0.0.1:5000/"),
            RunLogSelfEndpoint.Choose(["unix:/tmp/zenkins.sock", "http://127.0.0.1:5000"]));
    }

    [Fact]
    public void A_host_that_reports_no_address_resolves_to_nothing()
    {
        // Null is the caller's cue to fall back to the browser's base URI (TestServer, IIS in-process).
        Assert.Null(RunLogSelfEndpoint.Choose(null));
        Assert.Null(RunLogSelfEndpoint.Choose([]));
    }

    [Fact]
    public async Task The_resolved_address_reaches_the_listening_process()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // A real Kestrel listener told to bind every interface on a dynamic port: the shape that made
        // the old browser-URL round trip fail. The resolved URL must be a loopback one that answers.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://0.0.0.0:0");
        builder.Services.AddSingleton<RunLogSelfEndpoint>();

        await using var app = builder.Build();
        app.MapPost("/hubs/run-logs/negotiate", () => Results.Ok(new { negotiated = true }));

        await app.StartAsync(cancellationToken);
        try
        {
            var endpoint = app.Services.GetRequiredService<RunLogSelfEndpoint>();
            var hubUrl = endpoint.RunLogHubUrl;

            Assert.NotNull(hubUrl);
            Assert.Equal("127.0.0.1", hubUrl.Host);
            Assert.Equal(
                "/hubs/run-logs",
                hubUrl.AbsolutePath);

            using var client = new HttpClient();
            using var response = await client.PostAsync(
                new Uri(hubUrl.AbsoluteUri + "/negotiate"),
                content: null,
                cancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await app.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task The_application_host_registers_the_endpoint()
    {
        // The panel resolves it from DI on every render; a missing registration would surface as a
        // failed page, not as a log stream error.
        using var host = new AuthTestHost();

        var endpoint = await host.InScopeAsync(services => Task.FromResult(
            services.GetRequiredService<RunLogSelfEndpoint>()));

        Assert.NotNull(endpoint);

        // The test host (TestServer) reports no listener addresses, so the panel keeps the
        // browser-base fallback there - exactly the documented behaviour.
        Assert.Null(endpoint.RunLogHubUrl);
    }
}
