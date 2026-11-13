using System.Net;
using System.Text;
using Xunit;
using Zenkins.Shared.Artifacts;

namespace Zenkins.Tests.Artifacts;

/// <summary>
/// Card F-016, TC-004 (1) and the review checklist's C-7/C-8: only the owning organization's members can
/// download, a not-finalized artifact is a plain 404, and nothing about the URL or a file name can inject a
/// header or reach a path outside the artifact.
/// </summary>
[Trait("Category", "Artifacts")]
public sealed class ArtifactDownloadAccessTests
{
    [Fact(Timeout = 120 * 1000)]
    public async Task A_member_of_another_organization_gets_a_bare_404_for_both_artifact_kinds()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();
        using var foreign = await host.Host.Inner.Inner.RegisterUserAsync("foreign-downloader@example.test");

        await host.WriteRepositoryTextAsync("out/one.txt", "file-artifact");
        await host.PublishAsync("out/one.txt", token);
        await host.WriteRepositoryTextAsync("dist/a.txt", "dir-artifact");
        await host.PublishAsync("dist", token);

        var artifacts = await host.ReadArtifactsAsync();
        Assert.Equal(2, artifacts.Count);

        foreach (var artifact in artifacts)
        {
            using var response = await host.DownloadAsync(artifact.Id, foreign);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(token));
        }
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task An_unauthenticated_request_is_refused_without_bytes()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();
        await host.WriteRepositoryTextAsync("out/one.txt", "secret-bytes");
        await host.PublishAsync("out/one.txt", token);
        var artifact = Assert.Single(await host.ReadArtifactsAsync());

        // The app's convention for a protected page is a redirect to sign-in (or a 401); either way, not a
        // single byte of the artifact may be returned.
        using var anonymous = host.Host.Host.CreateClient();
        using var response = await anonymous.GetAsync(
            $"/runs/{host.RunId}/artifacts/{artifact.Id}/download",
            token);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect
                or HttpStatusCode.Found or HttpStatusCode.NotFound,
            $"unexpected status {(int)response.StatusCode}");
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(token));
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task An_unknown_or_pending_artifact_is_a_404()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();

        using (var unknown = await host.DownloadAsync(Guid.NewGuid()))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        // A reserved-but-not-finalized artifact: the URL is right, the artifact is simply not there yet.
        var pendingId = await host.CreateArtifactAsync("pending.bin", ArtifactKind.File);
        using (var pending = await host.DownloadAsync(pendingId))
        {
            Assert.Equal(HttpStatusCode.NotFound, pending.StatusCode);
            Assert.Empty(await pending.Content.ReadAsByteArrayAsync(token));
        }
    }

    [Theory(Timeout = 120 * 1000)]
    [InlineData("../..")]
    [InlineData("%2e%2e%2f%2e%2e%2f")]
    [InlineData("%2Fetc%2Fpasswd")]
    [InlineData("..%5C..%5Cwindows")]
    public async Task A_traversal_in_the_download_url_is_a_404(string artifactSegment)
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();

        using var response = await host.OwnerClient.GetAsync(
            $"/runs/{host.RunId}/artifacts/{artifactSegment}/download",
            token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(token));
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task A_sibling_run_directory_with_the_same_prefix_is_never_read()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();
        await host.WriteRepositoryTextAsync("out/mine.txt", "mine");
        await host.PublishAsync("out/mine.txt", token);
        var artifact = Assert.Single(await host.ReadArtifactsAsync());

        // A directory whose name merely *starts with* this run's id: a prefix comparison would confuse the two;
        // the store builds paths from GUIDs, so it cannot.
        var sibling = Path.Combine(
            host.ArtifactRoot,
            artifact.OrganizationId.ToString("D"),
            artifact.RunId.ToString("D") + "X");
        Directory.CreateDirectory(sibling);
        await File.WriteAllTextAsync(Path.Combine(sibling, "mine.txt"), "sibling", token);

        using var download = await host.DownloadAsync(artifact.Id);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("mine", await download.Content.ReadAsStringAsync(token));
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task A_hostile_file_name_cannot_inject_a_header_or_a_second_file_name()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();

        // A name that would split a header if it were interpolated, plus a non-ASCII character. The path rules
        // allow the quotes (they are a legal file name) - the download name must still be safe.
        await host.WriteRepositoryTextAsync("out/re\"port-é.txt", "payload");
        await host.PublishAsync("out/re\"port-é.txt", token);
        var artifact = Assert.Single(await host.ReadArtifactsAsync());

        using var download = await host.DownloadAsync(artifact.Id);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);

        var disposition = download.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.DoesNotContain('\r', disposition!.ToString());
        Assert.DoesNotContain('\n', disposition.ToString());
        Assert.DoesNotContain("\"\"", disposition.ToString());
        Assert.False(download.Headers.Contains("X-Injected"), "a header was injected through the file name");
        Assert.Single(download.Content.Headers.GetValues("Content-Disposition"));
        Assert.Equal("payload", await download.Content.ReadAsStringAsync(token));
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task A_directory_download_is_named_after_the_artifact_and_is_sanitised()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();
        await host.WriteRepositoryTextAsync("dist/nested/a.txt", "a");
        await host.PublishAsync("dist/nested", token);
        var artifact = Assert.Single(await host.ReadArtifactsAsync());

        using var download = await host.DownloadAsync(artifact.Id);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);

        // "{artifactName}.zip", sanitised: no separator can survive into the header.
        var name = download.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.Equal("dist_nested.zip", name);
    }
}
