using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using Zenkins.Server.Artifacts;
using Zenkins.Shared.Artifacts;
using Zenkins.Worker.Artifacts;

namespace Zenkins.Tests.Artifacts;

/// <summary>
/// Card F-016, TC-002: path safety is validated <b>twice</b>, and the second validation stands on its own.
/// <para>
/// The first half drives the real Worker publisher (a pipeline's declared path) and asserts the step-level
/// refusal. The second half is the part that can catch a "shared code, single call site" mistake: it crafts
/// uploads by hand - a <b>valid worker token</b> and a relative path the publisher would never send - and
/// proves the <b>server</b> refuses them by itself, writes nothing outside the store, and leaves no
/// downloadable artifact.
/// </para>
/// </summary>
[Trait("Category", "Artifacts")]
public sealed class ArtifactUploadSafetyTests
{
    [Fact(Timeout = 120 * 1000)]
    public async Task The_worker_publisher_refuses_every_escape_vector_and_names_the_path()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();

        // The three vectors the card names: a relative traversal, an absolute path, a drive-qualified path.
        // (The symlink vector is a separate test: it needs a real link on disk.)
        var vectors = new[] { "../../etc/passwd", "/etc/passwd", @"C:\Windows\System32\config" };

        foreach (var vector in vectors)
        {
            var failure = await Assert.ThrowsAsync<ArtifactPublishException>(
                () => host.PublishAsync(vector, token));

            Assert.Equal(Zenkins.Shared.Pipelines.PipelineStepFailureReasons.ArtifactPathRejected, failure.FailureReason);
            Assert.Contains(vector, failure.Message, StringComparison.Ordinal);
        }

        Assert.Empty(await host.ReadArtifactsAsync());
        Assert.Empty(host.StoreFiles());
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task The_worker_publisher_refuses_a_symlink_that_leaves_the_repository_by_name()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();

        var outside = Path.Combine(Path.GetTempPath(), $"zenkins-f016-outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outside, "do not publish me", token);
        try
        {
            // A checkout is untrusted content: `ln -s /etc/passwd leak.txt` and commit is a real attack shape.
            var link = host.RepositoryPath("leak.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            CreateSymbolicLinkOrFail(link, outside);

            var failure = await Assert.ThrowsAsync<ArtifactPublishException>(
                () => host.PublishAsync("leak.txt", token));

            Assert.Equal(Zenkins.Shared.Pipelines.PipelineStepFailureReasons.ArtifactPathRejected, failure.FailureReason);
            Assert.Contains("leak.txt", failure.Message, StringComparison.Ordinal);
            Assert.Contains("links outside the checked-out repository", failure.Message, StringComparison.Ordinal);
            Assert.Empty(await host.ReadArtifactsAsync());
        }
        finally
        {
            if (File.Exists(outside))
            {
                File.Delete(outside);
            }
        }
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task A_symlink_that_stays_inside_the_repository_is_published_as_its_target()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();
        await host.WriteRepositoryTextAsync("real/data.txt", "inside");
        CreateSymbolicLinkOrFail(host.RepositoryPath("alias.txt"), host.RepositoryPath("real/data.txt"));

        // A link inside the checkout is not an escape; it is published with its content, under the name it was
        // declared with.
        await host.PublishAsync("alias.txt", token);

        var artifact = Assert.Single(await host.ReadArtifactsAsync());
        using var download = await host.DownloadAsync(artifact.Id);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("inside", await download.Content.ReadAsStringAsync(token));
    }

    [Theory(Timeout = 120 * 1000)]
    [InlineData("../escape.txt")]
    [InlineData("%2e%2e%2fescape.txt")]
    [InlineData("..%2f..%2fescape.txt")]
    [InlineData("%2Fetc%2Fpasswd")]
    [InlineData("C%3A%5CWindows%5CSystem32%5Cconfig")]
    [InlineData("bin%2F..%2F..%2Fescape.txt")]
    public async Task The_server_refuses_a_hand_crafted_upload_path_on_its_own(string encodedPath)
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();
        var artifactId = await host.CreateArtifactAsync("crafted", ArtifactKind.File);
        var bytes = Encoding.UTF8.GetBytes("hostile payload");
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        // A real worker token, a real run, a real endpoint - only the relative path is a lie the publisher
        // would never tell. If the server trusted the worker, this is where a file outside the store appears.
        using var response = await host.UploadFileRawAsync(artifactId, encodedPath, bytes, sha);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(token);
        var error = await response.Content.ReadFromJsonAsync<ArtifactEndpoints.ArtifactErrorResponse>(token);
        Assert.True(error is not null, $"status {(int)response.StatusCode}, body '{body}'");
        Assert.Equal(ArtifactErrorCodes.PathRejected, error!.Error);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Nothing was written anywhere: not into the store, not into the run's directory, not next to it.
        Assert.Empty(host.StoreFiles());
        Assert.False(File.Exists(Path.Combine(host.Work.RootPath, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(host.Work.ArtifactsPath, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(host.ArtifactRoot, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(host.ArtifactRoot)!, "escape.txt")));

        // And the artifact row is not Ready: a refused upload can never be downloaded.
        var artifact = await host.ReadArtifactAsync(artifactId);
        Assert.NotNull(artifact);
        Assert.NotEqual(ArtifactStatus.Ready, artifact!.Status);

        using var download = await host.DownloadAsync(artifactId);
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task A_crafted_finalize_cannot_smuggle_a_path_into_the_manifest()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();
        var artifactId = await host.CreateArtifactAsync("crafted-finalize", ArtifactKind.File);

        using var response = await host.FinalizeArtifactAsync(
            artifactId,
            new ArtifactFileEntry("../escape.txt", new string('a', 64), 1),
            new ArtifactFileEntry("/etc/passwd", new string('b', 64), 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var artifact = await host.ReadArtifactAsync(artifactId);
        Assert.NotEqual(ArtifactStatus.Ready, artifact!.Status);
        Assert.Empty(host.StoreFiles());
        Assert.False(token.IsCancellationRequested);
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task A_wrong_hash_is_refused_and_the_partial_file_is_removed()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();
        var artifactId = await host.CreateArtifactAsync("hashed", ArtifactKind.File);
        var bytes = Encoding.UTF8.GetBytes("payload");

        using var response = await host.UploadFileRawAsync(artifactId, "payload.bin", bytes, new string('0', 64));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(token);
        var error = await response.Content.ReadFromJsonAsync<ArtifactEndpoints.ArtifactErrorResponse>(token);
        Assert.True(error is not null, $"status {(int)response.StatusCode}, body '{body}'");
        Assert.Equal(ArtifactErrorCodes.HashMismatch, error!.Error);
        Assert.Empty(host.StoreFiles());

        var artifact = await host.ReadArtifactAsync(artifactId);
        Assert.Equal(ArtifactStatus.Incomplete, artifact!.Status);
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task An_upload_without_a_token_is_unauthorized_and_writes_nothing()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();
        var artifactId = await host.CreateArtifactAsync("unauthenticated", ArtifactKind.File);
        var bytes = Encoding.UTF8.GetBytes("payload");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/workers/runs/{host.RunId}/artifacts/{artifactId}/files?path=payload.bin")
        {
            Content = new ByteArrayContent(bytes),
        };
        using var response = await host.WorkerClient.SendAsync(request, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(host.StoreFiles());
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task A_worker_cannot_publish_to_another_organizations_run()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();
        using var foreign = await host.Host.Inner.Inner.RegisterUserAsync("foreign-artifacts@example.test");

        await host.WriteRepositoryTextAsync("out/x.txt", "x");
        await host.PublishAsync("out/x.txt", token);
        var artifact = Assert.Single(await host.ReadArtifactsAsync());

        // A member of another organization (no membership in this run's organization): the run does not exist
        // for them, so the download is a plain 404 with no bytes.
        using var download = await host.DownloadAsync(artifact.Id, foreign);
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        Assert.Empty(await download.Content.ReadAsByteArrayAsync(token));
    }

    /// <summary>
    /// The MAJOR-1 regression (card F-016): an escaping symlink in an <b>intermediate</b> component. A leaf-only
    /// resolution sees no link at all here - <c>link/secret.txt</c> is an ordinary file name once <c>link</c> is
    /// followed - so this is the vector that publishes a file from outside the checkout if the check is wrong.
    /// </summary>
    [Fact(Timeout = 120 * 1000)]
    public async Task An_escaping_symlink_in_an_intermediate_component_is_refused()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();

        var outside = Path.Combine(Path.GetTempPath(), $"zenkins-f016-outside-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "SECRET-OUTSIDE", token);
        Directory.CreateDirectory(Path.Combine(outside, "nested"));
        await File.WriteAllTextAsync(Path.Combine(outside, "nested", "file.bin"), "SECRET-NESTED", token);
        try
        {
            // CASE 1: link/secret.txt - the *parent* directory is the link.
            CreateSymbolicLinkOrFail(host.RepositoryPath("link"), outside);

            var first = await Assert.ThrowsAsync<ArtifactPublishException>(
                () => host.PublishAsync("link/secret.txt", token));
            Assert.Equal(Zenkins.Shared.Pipelines.PipelineStepFailureReasons.ArtifactPathRejected, first.FailureReason);
            Assert.Contains("link/secret.txt", first.Message, StringComparison.Ordinal);
            Assert.Contains("links outside the checked-out repository", first.Message, StringComparison.Ordinal);

            // CASE 2: dist/link -> outside, declared as dist/link/file.bin - the link is one intermediate
            // component among several.
            var dist = host.RepositoryPath("dist");
            Directory.CreateDirectory(dist);
            CreateSymbolicLinkOrFail(Path.Combine(dist, "link"), outside);

            var second = await Assert.ThrowsAsync<ArtifactPublishException>(
                () => host.PublishAsync("dist/link/nested/file.bin", token));
            Assert.Equal(Zenkins.Shared.Pipelines.PipelineStepFailureReasons.ArtifactPathRejected, second.FailureReason);
            Assert.Contains("dist/link/nested/file.bin", second.Message, StringComparison.Ordinal);

            // A file artifact declared through the escaping link publishes nothing at all: no row, no bytes.
            var third = await Assert.ThrowsAsync<ArtifactPublishException>(
                () => host.PublishAsync("dist/link/nested", token));
            Assert.Equal(Zenkins.Shared.Pipelines.PipelineStepFailureReasons.ArtifactPathRejected, third.FailureReason);

            Assert.Empty(await host.ReadArtifactsAsync());
            Assert.Empty(host.StoreFiles());
        }
        finally
        {
            try
            {
                Directory.Delete(outside, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// A nested link that stays inside the checkout is followed and published: the per-component resolution must
    /// not turn "any link" into a refusal, only an escaping one.
    /// </summary>
    [Fact(Timeout = 120 * 1000)]
    public async Task A_nested_symlink_that_stays_inside_is_published_as_its_target()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();
        await host.WriteRepositoryTextAsync("real/nested/data.txt", "inside-nested");

        var link = host.RepositoryPath("linked");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        CreateSymbolicLinkOrFail(link, host.RepositoryPath("real"));

        await host.PublishAsync("linked/nested/data.txt", token);

        var artifact = Assert.Single(await host.ReadArtifactsAsync());
        using var download = await host.DownloadAsync(artifact.Id);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("inside-nested", await download.Content.ReadAsStringAsync(token));
    }

    /// <summary>
    /// Creates a symbolic link or fails the test with the reason. The macOS/Linux temp directory allows this, so a
    /// refusal here is an environment problem that must be visible - silently returning would make the symlink
    /// vectors pass without asserting anything.
    /// </summary>
    private static void CreateSymbolicLinkOrFail(string linkPath, string targetPath)
    {
        try
        {
            // The API matches the target's kind, which keeps the fixture honest on every platform.
            if (Directory.Exists(targetPath))
            {
                Directory.CreateSymbolicLink(linkPath, targetPath);
            }
            else
            {
                File.CreateSymbolicLink(linkPath, targetPath);
            }
        }
        catch (Exception failure) when (failure is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Fail(
                $"the test needs a symbolic link ('{linkPath}' → '{targetPath}') and this host refused: "
                + $"{failure.GetType().Name}: {failure.Message}. The symlink vectors cannot be skipped.");
        }
    }
}
