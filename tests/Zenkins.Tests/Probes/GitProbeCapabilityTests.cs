using System.Net;
using System.Text.RegularExpressions;
using Xunit;
using Zenkins.Tests.Workers;

namespace Zenkins.Tests.Probes;

/// <summary>
/// Card I-005's capability facts: the flag that keeps a Worker built before this card from ever being
/// handed a probe. Two promises, each pinned separately because they are two different payloads:
/// <list type="bullet">
/// <item><b>Registration states what the Worker is now.</b> A pre-I-005 binary sends no field, which reads as
/// "no" - so re-registering over a probe-capable row must clear the flag instead of inheriting a promise the
/// old binary cannot keep.</item>
/// <item><b>A heartbeat only refreshes when it says something.</b> An old Worker's heartbeat omits the field
/// every interval; treating that as "false" would revoke the capability a new registration just granted, so
/// absence leaves the stored value alone.</item>
/// </list>
/// </summary>
public sealed class GitProbeCapabilityTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Re_registering_without_the_capability_clears_it_and_the_page_says_so()
    {
        using var host = await GitProbeTestHost.CreateAsync();
        using var owner = await host.Inner.SignInOwnerAsync();
        const string name = "capability-agent";

        // A probe-capable worker first: the row (and the worker detail page) say yes.
        var (_, workerId) = await RegisterAsync(host, owner, name, canProbe: true);
        Assert.True((await host.Inner.ReadWorkerAsync(workerId)).CanProbe);
        Assert.Equal("Yes", await ReadCanProbeFromPageAsync(host, owner, workerId));

        // Then the same name re-registers the way a pre-I-005 binary does - with no canProbe field at all.
        using (var registered = await WorkerTestHost.RegisterAsync(
            owner,
            await MintRegistrationTokenAsync(host, owner),
            OldWorkerPayload(name)))
        {
            Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        }

        var worker = await host.Inner.ReadWorkerAsync(workerId);
        Assert.False(
            worker.CanProbe,
            "A re-registration that says nothing about probing must clear the capability, never inherit it.");
        Assert.Equal("No", await ReadCanProbeFromPageAsync(host, owner, workerId));
    }

    [Fact]
    public async Task A_heartbeat_without_the_capability_field_keeps_the_stored_value()
    {
        using var host = await GitProbeTestHost.CreateAsync();
        var (workerToken, workerId) = await host.RegisterWorkerAsync("heartbeat-agent", canProbe: true);
        using var workerClient = host.CreateWorkerClient();

        // An old Worker's heartbeat body: the field is not there at all.
        using (var heartbeat = await WorkerTestHost.HeartbeatAsync(
            workerClient,
            workerToken,
            OldWorkerPayload(name: null)))
        {
            Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        }

        Assert.True(
            (await host.Inner.ReadWorkerAsync(workerId)).CanProbe,
            "A heartbeat that does not mention probing must leave the registered capability alone.");

        // A new Worker that turned probing off does say so - and that does revoke it.
        using (var heartbeat = await WorkerTestHost.HeartbeatAsync(
            workerClient,
            workerToken,
            WorkerTestHost.HeartbeatPayload(os: "Linux", cpuCount: 4, maxConcurrency: 1, canProbe: false)))
        {
            Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        }

        Assert.False((await host.Inner.ReadWorkerAsync(workerId)).CanProbe);
    }

    /// <summary>
    /// Exactly what a Worker built before card I-005 posts: the PRD §3.4.2 fields and no <c>canProbe</c>
    /// (a registration payload carrying the key with a null value would be the *new* binary's shape).
    /// </summary>
    private static object OldWorkerPayload(string? name) => name is null
        ? new { os = "Linux", architecture = "X64", version = "1.0.0", tags = "ci", cpuCount = 4, maxConcurrency = 1 }
        : new { name, os = "Linux", architecture = "X64", version = "1.0.0", tags = "ci", cpuCount = 4, maxConcurrency = 1 };

    private static async Task<(string Token, Guid WorkerId)> RegisterAsync(
        GitProbeTestHost host,
        HttpClient owner,
        string name,
        bool canProbe)
    {
        using var registered = await WorkerTestHost.RegisterAsync(
            owner,
            await MintRegistrationTokenAsync(host, owner),
            WorkerTestHost.RegistrationPayload(
                name: name,
                tags: "ci",
                maxConcurrency: 1,
                canProbe: canProbe));
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var token = await WorkerTestHost.ReadStringPropertyAsync(registered, "workerToken");
        var workerId = Guid.Parse(await WorkerTestHost.ReadStringPropertyAsync(registered, "workerId"));
        return (token, workerId);
    }

    private static async Task<string> MintRegistrationTokenAsync(GitProbeTestHost host, HttpClient owner)
    {
        var (registrationToken, response) = await WorkerTestHost.MintRegistrationTokenAsync(owner);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return registrationToken;
    }

    /// <summary>The worker detail page's own words for the capability (the operator's view of it).</summary>
    private static async Task<string> ReadCanProbeFromPageAsync(
        GitProbeTestHost host,
        HttpClient owner,
        Guid workerId)
    {
        var page = await Zenkins.Tests.Auth.AuthTestHost.GetStringAsync(owner, $"/workers/{workerId}");
        var match = Regex.Match(page, "id=\"worker-can-probe\">\\s*(?<value>Yes|No)", RegexOptions.CultureInvariant);
        Assert.True(match.Success, "The worker detail page did not state its remote-probe capability.");
        return match.Groups["value"].Value;
    }
}
