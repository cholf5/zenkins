using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Card Q-006's probes of the artifact feature (F-016) re-verified from the audit: the worker API
/// refuses a foreign run or artifact with the same bare 404 as an unknown one, the path rules refuse
/// every traversal spelling, a finalized artifact of org B is not downloadable by org A's session,
/// and the store layout itself keeps every organization inside its own subtree. Both organizations
/// publish real artifacts through the real worker API first, so the refusals below are measured
/// against material that actually exists on disk. Each test owns its own environment (fresh
/// database and store) because it claims the fixture's runs and writes to the store.
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantIsolationArtifactUploadProbeTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public const string AlphaArtifactMarker = "alpha-artifact-bytes-52d90f";
    public const string BetaArtifactMarker = "beta-artifact-bytes-8a17c3";

    [Fact]
    public async Task The_artifact_worker_api_refuses_a_foreign_run_or_artifact()
    {
        using var host = new TenantProbeHostFixture();
        var (alphaArtifactId, betaArtifactId, alphaClient, betaClient) =
            await PublishBothAsync(host, "refusal");

        // Org A's token against org B's run: create, upload and finalize are all the same bare 404.
        using (var create = await alphaClient.PostAsync(
                   $"/api/workers/runs/{TwoOrgsFixture.BuildRunBId}/artifacts",
                   new StringContent("""{"name":"smuggled","kind":"File"}""", Encoding.UTF8, "application/json"),
                   CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
        }

        using (var upload = await alphaClient.PostAsync(
                   $"/api/workers/runs/{TwoOrgsFixture.BuildRunBId}/artifacts/{betaArtifactId}/files?path=smuggled.txt",
                   new ByteArrayContent(Encoding.UTF8.GetBytes("smuggled-bytes")),
                   CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, upload.StatusCode);
        }

        using (var finalize = await alphaClient.PostAsync(
                   $"/api/workers/runs/{TwoOrgsFixture.BuildRunBId}/artifacts/{betaArtifactId}/finalize",
                   new StringContent("""{"files":[]}""", Encoding.UTF8, "application/json"),
                   CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, finalize.StatusCode);
        }

        // The reverse direction: org B's token against org A's run and artifact.
        using (var reverse = await betaClient.PostAsync(
                   $"/api/workers/runs/{TwoOrgsFixture.BuildRunAId}/artifacts/{alphaArtifactId}/finalize",
                   new StringContent("""{"files":[]}""", Encoding.UTF8, "application/json"),
                   CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, reverse.StatusCode);
        }

        // Org A's published artifact is still Ready and complete: the refused verbs wrote nothing.
        var alphaArtifact = (await TenantIsolationAuditProbes.ReadAcrossTenantsAsync<Artifact>(
            host, artifact => artifact.Id == alphaArtifactId)).Single();
        Assert.Equal("Ready", alphaArtifact.Status);
        Assert.Equal(1, alphaArtifact.FileCount);
    }

    [Fact]
    public async Task Artifact_paths_cannot_escape_or_enter_another_artifacts_tree()
    {
        using var host = new TenantProbeHostFixture();

        // A PENDING artifact (reserved through the real API, never finalized) - the traversal
        // spellings must be refused by the path rules themselves, not by the not-Pending conflict.
        var (_, alphaToken) = await TenantIsolationWorkerApiProbes.RegisterAlphaWorkerAsync(host);
        using var alphaClient = host.CreateClient();
        alphaClient.DefaultRequestHeaders.Authorization = new("Bearer", alphaToken);
        await TenantIsolationWorkerApiProbes.ClaimAndAcceptAsync(alphaClient, TwoOrgsFixture.BuildRunAId);
        using var create = await alphaClient.PostAsync(
            $"/api/workers/runs/{TwoOrgsFixture.BuildRunAId}/artifacts",
            new StringContent("""{"name":"alpha-pending","kind":"Directory"}""", Encoding.UTF8, "application/json"),
            CancellationToken);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var createdPayload = JsonDocument.Parse(
            await create.Content.ReadAsStringAsync(CancellationToken));
        var artifactId = createdPayload.RootElement.GetProperty("artifactId").GetGuid();

        // Every traversal spelling is refused with the F-016 rules' 400. The last two spellings are
        // the raw WIRE forms an attacker sends (single URL-encoded, left unescaped here so the
        // transport decodes them into '..' segments before the rules see them).
        foreach (var (queryPath, description) in new[]
                 {
                     ("../beta-escape.txt", "parent segment"),
                     ("..\\beta-escape.txt", "backslash parent"),
                     ("alpha-pending/../../beta-escape.txt", "mid-path parent"),
                     ("/etc/zenkins-escape.txt", "absolute path"),
                 })
        {
            using var response = await alphaClient.PostAsync(
                $"/api/workers/runs/{TwoOrgsFixture.BuildRunAId}/artifacts/{artifactId}/files"
                + $"?path={Uri.EscapeDataString(queryPath)}&sha256={Sha256Hex("escape")}",
                new ByteArrayContent(Encoding.UTF8.GetBytes("escape")),
                CancellationToken);
            Assert.True(
                (int)response.StatusCode == 400,
                $"The traversal spelling '{description}' was not refused as a path error (got {(int)response.StatusCode}).");
        }

        foreach (var wirePath in new[] { "%2e%2e%2fbeta-escape.txt", "..%2Fbeta-escape.txt" })
        {
            using var response = await alphaClient.PostAsync(
                $"/api/workers/runs/{TwoOrgsFixture.BuildRunAId}/artifacts/{artifactId}/files"
                + $"?path={wirePath}&sha256={Sha256Hex("escape")}",
                new ByteArrayContent(Encoding.UTF8.GetBytes("escape")),
                CancellationToken);
            Assert.True(
                (int)response.StatusCode == 400,
                $"The wire-encoded traversal '{wirePath}' was not refused (got {(int)response.StatusCode}).");
        }

        // A spelling that normalizes away ('....//') may be accepted, but the file it lands must
        // stay inside the artifact's own directory - and nothing may ever appear outside it.
        using var normalized = await alphaClient.PostAsync(
            $"/api/workers/runs/{TwoOrgsFixture.BuildRunAId}/artifacts/{artifactId}/files"
            + $"?path={Uri.EscapeDataString("....//beta-normalized.txt")}&sha256={Sha256Hex("escape")}",
            new ByteArrayContent(Encoding.UTF8.GetBytes("escape")),
            CancellationToken);

        // The store audit: every byte in the tree sits inside one artifact's own directory, under
        // the (org, run, artifact) layout - no escape file exists under any name.
        var root = host.Data.ArtifactsPath;
        Assert.True(Directory.Exists(root));
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("beta-escape", Path.GetFileName(file), StringComparison.Ordinal);
            Assert.DoesNotContain("zenkins-escape", Path.GetFileName(file), StringComparison.Ordinal);
            var relative = Path.GetRelativePath(root, file);
            Assert.StartsWith(
                Path.Combine(TwoOrgsFixture.OrgAId.ToString("D"), TwoOrgsFixture.BuildRunAId.ToString("D"), artifactId.ToString("D"))
                    + Path.DirectorySeparatorChar,
                relative,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_foreign_organizations_artifact_is_not_downloadable()
    {
        using var host = new TenantProbeHostFixture();
        var (alphaArtifactId, betaArtifactId, _, _) = await PublishBothAsync(host, "download", kind: "File");

        // Org A's session against org B's artifact (and org B's against org A's): bare 404 with no
        // bytes - the same answer an unknown artifact id gets, so there is no existence oracle.
        using var alice = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);
        using var carol = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgBOwnerEmail);

        using (var foreignForAlice = await alice.GetAsync(
                   $"/runs/{TwoOrgsFixture.BuildRunBId}/artifacts/{betaArtifactId}/download",
                   CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignForAlice.StatusCode);
            Assert.Empty(await foreignForAlice.Content.ReadAsByteArrayAsync(CancellationToken));
        }

        using (var foreignForCarol = await carol.GetAsync(
                   $"/runs/{TwoOrgsFixture.BuildRunAId}/artifacts/{alphaArtifactId}/download",
                   CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignForCarol.StatusCode);
            Assert.Empty(await foreignForCarol.Content.ReadAsByteArrayAsync(CancellationToken));
        }

        // Positive control: org A's own artifact downloads with exactly org A's bytes - org B's
        // marker never rides along. (A File-kind artifact downloads raw, so the marker is verbatim.)
        var own = await AuthTestHost.GetStringAsync(
            alice,
            $"/runs/{TwoOrgsFixture.BuildRunAId}/artifacts/{alphaArtifactId}/download");
        Assert.Contains(AlphaArtifactMarker, own, StringComparison.Ordinal);
        Assert.DoesNotContain(BetaArtifactMarker, own, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_artifact_store_layout_keeps_organizations_in_their_own_subtrees()
    {
        using var host = new TenantProbeHostFixture();
        var (alphaArtifactId, betaArtifactId, _, _) = await PublishBothAsync(host, "layout");

        // The on-disk layout is exactly {root}/{org}/{run}/{artifact}/...: org A's tree contains
        // only org A's ids, org B's only org B's, and neither tree contains the other's bytes.
        var root = host.Data.ArtifactsPath;
        var alphaTree = Path.Combine(root, TwoOrgsFixture.OrgAId.ToString("D"));
        var betaTree = Path.Combine(root, TwoOrgsFixture.OrgBId.ToString("D"));

        Assert.True(Directory.Exists(alphaTree));
        Assert.True(Directory.Exists(betaTree));
        Assert.Equal(
            new[] { TwoOrgsFixture.BuildRunAId.ToString("D") },
            Directory.GetDirectories(alphaTree).Select(Path.GetFileName));
        Assert.Equal(
            new[] { alphaArtifactId.ToString("D") },
            Directory.GetDirectories(Path.Combine(alphaTree, TwoOrgsFixture.BuildRunAId.ToString("D")))
                .Select(Path.GetFileName));
        Assert.Equal(
            new[] { TwoOrgsFixture.BuildRunBId.ToString("D") },
            Directory.GetDirectories(betaTree).Select(Path.GetFileName));
        Assert.Equal(
            new[] { betaArtifactId.ToString("D") },
            Directory.GetDirectories(Path.Combine(betaTree, TwoOrgsFixture.BuildRunBId.ToString("D")))
                .Select(Path.GetFileName));

        // The bytes are tenant-separated: org A's tree carries org A's marker and never org B's,
        // and the mirror image holds for org B's tree.
        var alphaContents = Directory.EnumerateFiles(alphaTree, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText).ToList();
        Assert.Contains(alphaContents, content => content.Contains(AlphaArtifactMarker, StringComparison.Ordinal));
        Assert.DoesNotContain(alphaContents, content => content.Contains(BetaArtifactMarker, StringComparison.Ordinal));

        var betaContents = Directory.EnumerateFiles(betaTree, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText).ToList();
        Assert.Contains(betaContents, content => content.Contains(BetaArtifactMarker, StringComparison.Ordinal));
        Assert.DoesNotContain(betaContents, content => content.Contains(AlphaArtifactMarker, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------
    // The probe's own setup: both organizations publish one real artifact each
    // ---------------------------------------------------------------------

    /// <summary>
    /// Registers the probe's workers, claims and accepts both organizations' runs, and publishes
    /// one real artifact per organization through the worker API. Returns the two artifact ids and
    /// the two ready-to-use worker clients (org A's first).
    /// </summary>
    private async Task<(Guid AlphaArtifactId, Guid BetaArtifactId, HttpClient AlphaClient, HttpClient BetaClient)>
        PublishBothAsync(TenantProbeHostFixture host, string namePrefix, string kind = "Directory")
    {
        var (alphaId, alphaToken) = await PublishAsync(host, orgB: false, $"alpha-{namePrefix}", kind);
        var (betaId, betaToken) = await PublishAsync(host, orgB: true, $"beta-{namePrefix}", kind);

        var alphaClient = host.CreateClient();
        alphaClient.DefaultRequestHeaders.Authorization = new("Bearer", alphaToken);
        var betaClient = host.CreateClient();
        betaClient.DefaultRequestHeaders.Authorization = new("Bearer", betaToken);
        return (alphaId, betaId, alphaClient, betaClient);
    }

    /// <summary>Publishes one artifact of one organization through the real worker API.</summary>
    private async Task<(Guid ArtifactId, string WorkerToken)> PublishAsync(
        TenantProbeHostFixture host, bool orgB, string name, string kind)
    {
        var (_, workerToken) = orgB
            ? await TenantIsolationWorkerApiProbes.RegisterBetaWorkerAsync(host)
            : await TenantIsolationWorkerApiProbes.RegisterAlphaWorkerAsync(host);
        var runId = orgB ? TwoOrgsFixture.BuildRunBId : TwoOrgsFixture.BuildRunAId;

        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", workerToken);
        await TenantIsolationWorkerApiProbes.ClaimAndAcceptAsync(client, runId);

        using var create = await client.PostAsync(
            $"/api/workers/runs/{runId}/artifacts",
            new StringContent($$"""{"name":"{{name}}","kind":"{{kind}}"}""", Encoding.UTF8, "application/json"),
            CancellationToken);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var created = JsonDocument.Parse(
            await create.Content.ReadAsStringAsync(CancellationToken));
        var artifactId = created.RootElement.GetProperty("artifactId").GetGuid();

        var bytes = Encoding.UTF8.GetBytes(orgB ? BetaArtifactMarker : AlphaArtifactMarker);
        var relativePath = $"{name}/index.html";
        using var upload = await client.PostAsync(
            $"/api/workers/runs/{runId}/artifacts/{artifactId}/files"
            + $"?path={Uri.EscapeDataString(relativePath)}&sha256={Sha256Hex(bytes)}",
            new ByteArrayContent(bytes),
            CancellationToken);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var manifest = $$"""
            {"files":[{"path":"{{relativePath}}","sha256":"{{Sha256Hex(bytes)}}","bytes":{{bytes.Length}}}]}
            """;
        using var finalize = await client.PostAsync(
            $"/api/workers/runs/{runId}/artifacts/{artifactId}/finalize",
            new StringContent(manifest, Encoding.UTF8, "application/json"),
            CancellationToken);
        Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);

        return (artifactId, workerToken);
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Sha256Hex(string text) =>
        Sha256Hex(Encoding.UTF8.GetBytes(text));
}
