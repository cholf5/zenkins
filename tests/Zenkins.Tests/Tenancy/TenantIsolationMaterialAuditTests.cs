using System.Net;
using System.Text;
using Xunit;
using Zenkins.Server.Data.Entities;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Card Q-006's material audit (TC-002): with REAL secret and Git credential material on both sides
/// (created through the Owner forms, encrypted by the real key ring) and real artifacts published
/// through the worker API, org A's surfaces - its log download, its artifact bytes, its pages - and
/// the shared database file itself contain no byte of org B's plaintext material, and the mirror
/// image holds for org B. This is the filesystem-level check the audit card asks for: not only HTTP
/// status codes, but the bytes on disk.
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantIsolationMaterialAuditTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public const string AlphaSecretName = "ALPHA_AUDIT_TOKEN";
    public const string BetaSecretName = "BETA_AUDIT_TOKEN";
    public const string AlphaSecretValue = "alpha-material-secret-5ee1a3";
    public const string BetaSecretValue = "beta-material-secret-1c4b7f";
    public const string AlphaCredentialValue = "alpha-material-pat-d92f55";
    public const string BetaCredentialValue = "beta-material-pat-6a0e28";

    [Fact]
    public async Task No_other_tenants_credential_or_secret_material_appears_in_ones_own_surfaces()
    {
        using var host = new TenantProbeHostFixture();

        // Real tenant-owned material on both sides.
        using var alice = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgAOwnerEmail);
        using var carol = await TenantIsolationAuditProbes.SignInAsync(host, TenantIsolationProbeAssertions.OrgBOwnerEmail);
        await TenantIsolationAuditProbes.CreateSecretAsync(alice, AlphaSecretName, AlphaSecretValue);
        await TenantIsolationAuditProbes.CreateSecretAsync(carol, BetaSecretName, BetaSecretValue);
        await TenantIsolationAuditProbes.CreateGitCredentialAsync(alice, "ALPHA_AUDIT_PAT", "HttpsToken", AlphaCredentialValue);
        await TenantIsolationAuditProbes.CreateGitCredentialAsync(carol, "BETA_AUDIT_PAT", "HttpsToken", BetaCredentialValue);

        // Real artifacts on both sides, published through the worker API.
        var (alphaArtifactId, _) = await TenantIsolationMaterialAuditProbes.PublishArtifactAsync(host, orgB: false);
        var (betaArtifactId, _) = await TenantIsolationMaterialAuditProbes.PublishArtifactAsync(host, orgB: true);

        // ---- Org A's log download: org A's seeded line, never org B's material in any form.
        var alphaLog = await AuthTestHost.GetStringAsync(
            alice, $"/runs/{TwoOrgsFixture.BuildRunAId}/log/download");
        Assert.Contains("alpha-line", alphaLog, StringComparison.Ordinal);
        AssertExcludesBetaMaterial(alphaLog);

        // ---- Org A's artifact bytes: only org A's marker; org B's markers and material never.
        var alphaArtifact = await AuthTestHost.GetStringAsync(
            alice, $"/runs/{TwoOrgsFixture.BuildRunAId}/artifacts/{alphaArtifactId}/download");
        Assert.Contains(TenantIsolationMaterialAuditProbes.AlphaMarker, alphaArtifact, StringComparison.Ordinal);
        AssertExcludesBetaMaterial(alphaArtifact);

        // ---- Org A's pages: secrets page (names only), settings credential list, dashboard.
        var secretsPage = await AuthTestHost.GetStringAsync(alice, "/secrets");
        Assert.Contains(AlphaSecretName, secretsPage, StringComparison.Ordinal);
        AssertExcludesBetaMaterial(secretsPage);
        Assert.DoesNotContain(TwoOrgsFixture.SecretBCiphertext, secretsPage, StringComparison.Ordinal);

        var credentialList = await AuthTestHost.GetStringAsync(
            alice, $"/projects/{TwoOrgsFixture.ProjectAId}/git-credentials");
        Assert.Contains("ALPHA_AUDIT_PAT", credentialList, StringComparison.Ordinal);
        AssertExcludesBetaMaterial(credentialList);
        Assert.DoesNotContain("BETA_DEPLOY_TOKEN", credentialList, StringComparison.Ordinal);

        // ---- Org B's mirror image: none of org A's material in org B's surfaces.
        var betaLog = await AuthTestHost.GetStringAsync(
            carol, $"/runs/{TwoOrgsFixture.BuildRunBId}/log/download");
        Assert.Contains("beta-line", betaLog, StringComparison.Ordinal);
        AssertExcludesAlphaMaterial(betaLog);

        var betaArtifact = await AuthTestHost.GetStringAsync(
            carol, $"/runs/{TwoOrgsFixture.BuildRunBId}/artifacts/{betaArtifactId}/download");
        Assert.Contains(TenantIsolationMaterialAuditProbes.BetaMarker, betaArtifact, StringComparison.Ordinal);
        AssertExcludesAlphaMaterial(betaArtifact);

        var betaSecretsPage = await AuthTestHost.GetStringAsync(carol, "/secrets");
        Assert.Contains(BetaSecretName, betaSecretsPage, StringComparison.Ordinal);
        AssertExcludesAlphaMaterial(betaSecretsPage);

        // ---- The database file itself: credential and secret VALUES are never at rest in
        //      plaintext (F-012 stores Data Protection ciphertext, for every tenant alike). Row
        //      names and ordinary tenant rows legitimately share the file; the material values may
        //      not be readable there under any tenant.
        var databaseBytes = await TestFileRead.ReadHeldBytesAsync(host.Data.DatabasePath, CancellationToken);
        var databaseText = Encoding.Latin1.GetString(databaseBytes);
        Assert.DoesNotContain(BetaSecretValue, databaseText, StringComparison.Ordinal);
        Assert.DoesNotContain(BetaCredentialValue, databaseText, StringComparison.Ordinal);
        Assert.DoesNotContain(AlphaSecretValue, databaseText, StringComparison.Ordinal);
        Assert.DoesNotContain(AlphaCredentialValue, databaseText, StringComparison.Ordinal);

        // ---- And org A's stored rows are tenant-separated at the storage level: only org A's
        //      names are there, never a row of org B.
        var alphaRows = await TenantIsolationAuditProbes.ReadAcrossTenantsAsync<Secret>(
            host, secret => secret.OrganizationId == TwoOrgsFixture.OrgAId);
        Assert.All(alphaRows, row => Assert.NotEqual(BetaSecretName, row.Name));
        Assert.All(alphaRows, row => Assert.NotEqual("BETA_TOKEN", row.Name));
        var alphaCredentials = await TenantIsolationAuditProbes.ReadAcrossTenantsAsync<GitCredential>(
            host, credential => credential.OrganizationId == TwoOrgsFixture.OrgAId);
        Assert.All(alphaCredentials, row => Assert.NotEqual("BETA_AUDIT_PAT", row.Name));
        Assert.All(alphaCredentials, row => Assert.NotEqual(TwoOrgsFixture.GitCredentialBName, row.Name));
    }

    /// <summary>Every byte string of org B's plaintext material must be absent.</summary>
    private static void AssertExcludesBetaMaterial(string content)
    {
        Assert.DoesNotContain(BetaSecretValue, content, StringComparison.Ordinal);
        Assert.DoesNotContain(BetaCredentialValue, content, StringComparison.Ordinal);
        Assert.DoesNotContain(BetaSecretName, content, StringComparison.Ordinal);
        Assert.DoesNotContain("BETA_AUDIT_PAT", content, StringComparison.Ordinal);
        Assert.DoesNotContain("beta-line", content, StringComparison.Ordinal);
        Assert.DoesNotContain("beta-app", content, StringComparison.Ordinal);
    }

    /// <summary>Every byte string of org A's plaintext material must be absent (org B's mirror check).</summary>
    private static void AssertExcludesAlphaMaterial(string content)
    {
        Assert.DoesNotContain(AlphaSecretValue, content, StringComparison.Ordinal);
        Assert.DoesNotContain(AlphaCredentialValue, content, StringComparison.Ordinal);
        Assert.DoesNotContain(AlphaSecretName, content, StringComparison.Ordinal);
        Assert.DoesNotContain("ALPHA_AUDIT_PAT", content, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha-line", content, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha-app", content, StringComparison.Ordinal);
    }
}

/// <summary>
/// The material audit's own artifact publication (the same worker-API path the upload probes use),
/// kept separate so the two probe classes never share fixture state.
/// </summary>
public static class TenantIsolationMaterialAuditProbes
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public const string AlphaMarker = "alpha-artifact-material-77c1e9";
    public const string BetaMarker = "beta-artifact-material-3d5f02";

    public static async Task<(Guid ArtifactId, string WorkerToken)> PublishArtifactAsync(
        TenantProbeHostFixture host, bool orgB)
    {
        var (_, workerToken) = orgB
            ? await TenantIsolationWorkerApiProbes.RegisterBetaWorkerAsync(host)
            : await TenantIsolationWorkerApiProbes.RegisterAlphaWorkerAsync(host);
        var runId = orgB ? TwoOrgsFixture.BuildRunBId : TwoOrgsFixture.BuildRunAId;

        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", workerToken);
        await TenantIsolationWorkerApiProbes.ClaimAndAcceptAsync(client, runId);

        var name = orgB ? "beta-material" : "alpha-material";
        using var create = await client.PostAsync(
            $"/api/workers/runs/{runId}/artifacts",
            new StringContent($$"""{"name":"{{name}}","kind":"File"}""", Encoding.UTF8, "application/json"),
            CancellationToken);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var created = System.Text.Json.JsonDocument.Parse(
            await create.Content.ReadAsStringAsync(CancellationToken));
        var artifactId = created.RootElement.GetProperty("artifactId").GetGuid();

        var bytes = Encoding.UTF8.GetBytes(orgB ? BetaMarker : AlphaMarker);
        using var upload = await client.PostAsync(
            $"/api/workers/runs/{runId}/artifacts/{artifactId}/files"
            + $"?path={Uri.EscapeDataString(name + ".txt")}&sha256={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))}",
            new ByteArrayContent(bytes),
            CancellationToken);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var manifest = $$"""
            {"files":[{"path":"{{name}}.txt","sha256":"{{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))}}","bytes":{{bytes.Length}}}]}
            """;
        using var finalize = await client.PostAsync(
            $"/api/workers/runs/{runId}/artifacts/{artifactId}/finalize",
            new StringContent(manifest, Encoding.UTF8, "application/json"),
            CancellationToken);
        Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);

        return (artifactId, workerToken);
    }
}
