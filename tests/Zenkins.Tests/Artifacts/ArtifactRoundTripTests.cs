using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Xunit;
using Zenkins.Shared.Artifacts;

namespace Zenkins.Tests.Artifacts;

/// <summary>
/// Card F-016, TC-001: a step publishes a file and a directory through the <b>real</b> worker publisher and
/// the <b>real</b> server endpoints; both download byte-identically, and the run page lists them with sizes.
/// </summary>
[Trait("Category", "Artifacts")]
public sealed class ArtifactRoundTripTests
{
    [Fact(Timeout = 120 * 1000)]
    public async Task A_single_file_round_trips_byte_identically_with_its_own_name()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var host = await ArtifactsTestHost.CreateAsync();

        // 1 MB of deterministic binary content (not text: a text round-trip would hide an encoding bug).
        var bytes = DeterministicBytes(1024 * 1024, seed: 17);
        await host.WriteRepositoryFileAsync("bin/Release/sample.bin", bytes);

        await host.PublishAsync("bin/Release/sample.bin", TestContext.Current.CancellationToken);

        var artifact = Assert.Single(await host.ReadArtifactsAsync());
        Assert.Equal("bin/Release/sample.bin", artifact.Name);
        Assert.Equal(ArtifactKind.File, artifact.Kind);
        Assert.Equal(ArtifactStatus.Ready, artifact.Status);
        Assert.Equal(1, artifact.FileCount);
        Assert.Equal(bytes.Length, artifact.TotalBytes);

        using var download = await host.DownloadAsync(artifact.Id);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var downloaded = await download.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(bytes.Length, downloaded.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), Convert.ToHexStringLower(SHA256.HashData(downloaded)));

        // The original file name, not the wire path: "sample.bin".
        Assert.Equal(
            "sample.bin",
            download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task A_directory_round_trips_as_a_streamed_zip_that_restores_the_relative_tree()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var host = await ArtifactsTestHost.CreateAsync();

        await host.WriteRepositoryTextAsync("bin/Release/app.dll", "dll-bytes");
        await host.WriteRepositoryTextAsync("bin/Release/a/first.txt", "first");
        await host.WriteRepositoryTextAsync("bin/Release/a/b/second.txt", "second");

        await host.PublishAsync("bin/Release", TestContext.Current.CancellationToken);

        var artifact = Assert.Single(await host.ReadArtifactsAsync());
        Assert.Equal("bin/Release", artifact.Name);
        Assert.Equal(ArtifactKind.Directory, artifact.Kind);
        Assert.Equal(ArtifactStatus.Ready, artifact.Status);
        Assert.Equal(3, artifact.FileCount);
        Assert.Equal("dll-bytes".Length + "first".Length + "second".Length, artifact.TotalBytes);

        using var download = await host.DownloadAsync(artifact.Id);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);

        var archive = Path.Combine(Path.GetTempPath(), $"zenkins-f016-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var file = File.Create(archive))
            {
                await download.Content.CopyToAsync(file, TestContext.Current.CancellationToken);
            }

            using var zip = ZipFile.OpenRead(archive);
            Assert.Equal(
                ["a/b/second.txt", "a/first.txt", "app.dll"],
                zip.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal));

            Assert.All(zip.Entries, entry => Assert.False(Path.IsPathRooted(entry.FullName)));
            Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains("..", StringComparison.Ordinal));

            Assert.Equal("second", ReadEntry(zip, "a/b/second.txt"));
            Assert.Equal("first", ReadEntry(zip, "a/first.txt"));
            Assert.Equal("dll-bytes", ReadEntry(zip, "app.dll"));
        }
        finally
        {
            if (File.Exists(archive))
            {
                File.Delete(archive);
            }
        }
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task The_run_page_lists_the_artifacts_with_their_sizes()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var host = await ArtifactsTestHost.CreateAsync();
        await host.WriteRepositoryTextAsync("out/report.txt", "1234567890");
        await host.PublishAsync("out/report.txt", TestContext.Current.CancellationToken);

        var page = await host.ReadRunPageAsync();

        Assert.Contains("run-artifacts", page, StringComparison.Ordinal);
        Assert.Contains("out/report.txt", page, StringComparison.Ordinal);
        Assert.Contains("10 bytes", page, StringComparison.Ordinal);
        Assert.Contains($"/runs/{host.RunId}/artifacts/", page, StringComparison.Ordinal);
        Assert.Contains("/download", page, StringComparison.Ordinal);
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task A_second_artifact_with_the_same_name_in_the_same_run_is_refused()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var host = await ArtifactsTestHost.CreateAsync();
        await host.WriteRepositoryTextAsync("bin/app.dll", "dll");
        await host.PublishAsync("bin/app.dll", TestContext.Current.CancellationToken);

        // The same declared path twice in one run is a pipeline bug: the download links would be ambiguous.
        var second = await Assert.ThrowsAsync<Zenkins.Worker.Artifacts.ArtifactPublishException>(
            () => host.PublishAsync("bin/app.dll", TestContext.Current.CancellationToken));

        Assert.Contains("already publishes an artifact named", second.Message, StringComparison.Ordinal);
        Assert.Single(await host.ReadArtifactsAsync());
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task A_path_that_only_exists_under_the_run_artifacts_directory_is_not_publishable()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var host = await ArtifactsTestHost.CreateAsync();
        await host.WriteRunArtifactsDirectoryFileAsync("previous.bin", [1, 2, 3]);

        // The declared path resolves inside the checkout, where nothing exists: a previous run's artifacts
        // directory is not a source root (card F-016 decision 2).
        var failure = await Assert.ThrowsAsync<Zenkins.Worker.Artifacts.ArtifactPublishException>(
            () => host.PublishAsync("previous.bin", TestContext.Current.CancellationToken));

        Assert.Equal(Zenkins.Shared.Pipelines.PipelineStepFailureReasons.ArtifactPathRejected, failure.FailureReason);
        Assert.Contains("does not exist in the checked-out repository", failure.Message, StringComparison.Ordinal);
        Assert.Empty(await host.ReadArtifactsAsync());
    }

    private static byte[] DeterministicBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidOperationException($"zip entry '{name}' is missing");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
