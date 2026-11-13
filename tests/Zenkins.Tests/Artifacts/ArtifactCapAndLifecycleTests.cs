using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using Zenkins.Server.Artifacts;
using Zenkins.Shared.Artifacts;
using Zenkins.Worker.Artifacts;

namespace Zenkins.Tests.Artifacts;

/// <summary>
/// Card F-016, TC-003 and AC-7: every cap fails loudly and names its limit, a cap failure leaves nothing
/// downloadable, and an abandoned upload is closed by the sweeper instead of staying <c>Pending</c> forever.
/// </summary>
[Trait("Category", "Artifacts")]
public sealed class ArtifactCapAndLifecycleTests
{
    [Fact(Timeout = 120 * 1000)]
    public async Task The_server_refuses_a_file_over_the_per_file_cap_and_names_the_limit()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync(
            ArtifactsTestHost.Caps(maxFileBytes: 1024 * 1024, maxArtifactBytes: 2L * 1024 * 1024 * 1024, maxFiles: 20_000));
        await host.WriteRepositoryFileAsync("big.bin", DeterministicBytes(2 * 1024 * 1024, seed: 3));

        // The publisher's pre-check is deliberately given the defaults, so the *server's* configured cap is what
        // refuses: that is the authoritative side (decision 3).
        var failure = await Assert.ThrowsAsync<ArtifactPublishException>(
            () => host.PublishAsync("big.bin", token, ArtifactLimits.Defaults));

        Assert.Equal(Zenkins.Shared.Pipelines.PipelineStepFailureReasons.ArtifactFailed, failure.FailureReason);
        Assert.Contains("Artifacts:MaxFileBytes", failure.Message, StringComparison.Ordinal);
        Assert.Contains("big.bin", failure.Message, StringComparison.Ordinal);

        // Nothing partially uploaded is downloadable: the row is Incomplete, not Ready (decision 4).
        var artifact = Assert.Single(await host.ReadArtifactsAsync());
        Assert.Equal(ArtifactStatus.Incomplete, artifact.Status);
        Assert.NotEqual(ArtifactStatus.Ready, artifact.Status);

        using var download = await host.DownloadAsync(artifact.Id);
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task The_worker_pre_check_refuses_an_over_cap_file_before_uploading_it()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();
        await host.WriteRepositoryFileAsync("big.bin", DeterministicBytes(2 * 1024 * 1024, seed: 4));

        var publisher = host.CreatePublisher(limits: new ArtifactLimits(
            MaxFileBytes: 1024 * 1024,
            MaxArtifactBytes: 1L * 1024 * 1024 * 1024,
            MaxFiles: 100));

        var failure = await Assert.ThrowsAsync<ArtifactPublishException>(
            () => publisher.PublishAsync("big.bin", token));

        Assert.Contains("Artifacts:MaxFileBytes", failure.Message, StringComparison.Ordinal);
        Assert.Empty(await host.ReadArtifactsAsync());
        Assert.Empty(host.StoreFiles());
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task The_server_refuses_an_artifact_over_the_file_count_cap_and_names_the_limit()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync(
            ArtifactsTestHost.Caps(maxFileBytes: 1024 * 1024, maxArtifactBytes: 64L * 1024 * 1024, maxFiles: 3));

        for (var i = 0; i < 4; i++)
        {
            await host.WriteRepositoryTextAsync($"dist/file{i}.txt", $"content-{i}");
        }

        var failure = await Assert.ThrowsAsync<ArtifactPublishException>(
            () => host.PublishAsync("dist", token, ArtifactLimits.Defaults));

        Assert.Contains("Artifacts:MaxFiles", failure.Message, StringComparison.Ordinal);

        var artifact = Assert.Single(await host.ReadArtifactsAsync());
        Assert.NotEqual(ArtifactStatus.Ready, artifact.Status);

        using var download = await host.DownloadAsync(artifact.Id);
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task The_server_refuses_an_artifact_over_the_total_cap_and_names_the_limit()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync(
            ArtifactsTestHost.Caps(
                maxFileBytes: 4L * 1024 * 1024,
                maxArtifactBytes: 1536L * 1024,
                maxFiles: 100));

        await host.WriteRepositoryFileAsync("dist/one.bin", DeterministicBytes(1024 * 1024, seed: 5));
        await host.WriteRepositoryFileAsync("dist/two.bin", DeterministicBytes(1024 * 1024, seed: 6));

        var failure = await Assert.ThrowsAsync<ArtifactPublishException>(
            () => host.PublishAsync("dist", token, ArtifactLimits.Defaults));

        Assert.Contains("Artifacts:MaxArtifactBytes", failure.Message, StringComparison.Ordinal);

        var artifact = Assert.Single(await host.ReadArtifactsAsync());
        Assert.NotEqual(ArtifactStatus.Ready, artifact.Status);
        using var download = await host.DownloadAsync(artifact.Id);
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task An_abandoned_upload_is_left_un_downloadable_and_the_sweeper_closes_it()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();
        await host.WriteRepositoryFileAsync("dist/a.txt", Encoding.UTF8.GetBytes("a"));
        await host.WriteRepositoryFileAsync("dist/b.txt", Encoding.UTF8.GetBytes("b"));

        // A worker that dies between two files: the row exists, one file is on disk, nothing was finalized.
        var artifactId = await host.CreateArtifactAsync("dist", ArtifactKind.Directory);
        var bytes = Encoding.UTF8.GetBytes("a");
        using (var uploaded = await host.UploadFileRawAsync(
            artifactId,
            "a.txt",
            bytes,
            Convert.ToHexStringLower(SHA256.HashData(bytes))))
        {
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        }

        var pending = Assert.Single(await host.ReadArtifactsAsync());
        Assert.Equal(ArtifactStatus.Pending, pending.Status);

        // AC-6: a Pending artifact is never downloadable, so a half-uploaded directory cannot be served.
        using (var download = await host.DownloadAsync(artifactId))
        {
            Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        }

        // The sweeper's window has passed (the harness ages the row instead of waiting 30 minutes).
        await host.AgePendingArtifactsAsync(TimeSpan.FromMinutes(31));
        var sweep = await host.SweepAsync();

        Assert.Equal(1, sweep.IncompleteArtifacts);
        var swept = Assert.Single(await host.ReadArtifactsAsync());
        Assert.Equal(ArtifactStatus.Incomplete, swept.Status);

        // The bytes stay for diagnosis (decision 4) but the artifact is still not downloadable.
        Assert.NotEmpty(host.StoreFiles());
        using (var download = await host.DownloadAsync(artifactId))
        {
            Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        }

        // A second tick is a no-op: nothing is left Pending, so nothing is rewritten.
        Assert.Equal(0, (await host.SweepAsync()).IncompleteArtifacts);
        Assert.False(token.IsCancellationRequested);
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task A_canceled_upload_is_aborted_by_the_run_token_and_leaves_no_ready_row()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();
        await host.WriteRepositoryFileAsync("bin/canceled.bin", DeterministicBytes(8 * 1024 * 1024, seed: 9));

        // The run was canceled (or its deadline passed) while the artifact was being published: every call the
        // publisher makes carries the run's linked kill token, so the upload is aborted, not completed.
        var artifactId = await host.CreateArtifactAsync("bin/canceled.bin", ArtifactKind.File);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        var api = new Zenkins.Worker.WorkerApiClient(host.WorkerClient);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.UploadArtifactFileAsync(
            host.WorkerToken,
            host.RunId,
            artifactId,
            "bin/canceled.bin",
            new string('0', 64),
            host.RepositoryPath("bin/canceled.bin"),
            canceled.Token));

        // The row is not Ready, nothing was written, and the download is a 404: a canceled upload can never be
        // mistaken for a finished artifact.
        var artifact = Assert.Single(await host.ReadArtifactsAsync());
        Assert.NotEqual(ArtifactStatus.Ready, artifact.Status);
        Assert.Empty(host.StoreFiles());

        using var download = await host.DownloadAsync(artifactId);
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        Assert.False(token.IsCancellationRequested);
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task The_host_refuses_to_start_when_the_artifact_store_root_cannot_be_written()
    {
        var token = TestContext.Current.CancellationToken;

        // A file where the store's directory should go: creating the root must fail, and the failure must be a
        // startup error naming the store - not a surprise on the first upload of the first run.
        var blocker = Path.Combine(Path.GetTempPath(), $"zenkins-f016-blocker-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(blocker, "not a directory", token);
        try
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => ArtifactsTestHost.CreateAsync(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Artifacts:RootPath"] = Path.Combine(blocker, "artifacts"),
                }));

            var messages = Describe(failure);
            Assert.Contains("artifact store", messages, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not writable", messages, StringComparison.OrdinalIgnoreCase);

            // The remedy is per purpose: pointing an artifact-store failure at the Data Protection key ring would
            // send an operator to the wrong setting (and the wrong backup).
            Assert.Contains("Artifacts:RootPath", messages, StringComparison.Ordinal);
            Assert.DoesNotContain("key ring", messages, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DataProtection:KeysDirectory", messages, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(blocker))
            {
                File.Delete(blocker);
            }
        }
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task A_fresh_pending_upload_is_not_swept()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();

        // A worker that is still uploading: the row exists and one file landed seconds ago. Deleting the
        // staleness predicate would sweep this away mid-upload, so this is the negative case that pins it.
        var artifactId = await host.CreateArtifactAsync("dist", ArtifactKind.Directory);
        var bytes = Encoding.UTF8.GetBytes("a");
        using (var uploaded = await host.UploadFileRawAsync(
            artifactId,
            "a.txt",
            bytes,
            Convert.ToHexStringLower(SHA256.HashData(bytes))))
        {
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        }

        var sweep = await host.SweepAsync();

        Assert.Equal(0, sweep.IncompleteArtifacts);
        var artifact = Assert.Single(await host.ReadArtifactsAsync());
        Assert.Equal(ArtifactStatus.Pending, artifact.Status);
        Assert.False(token.IsCancellationRequested);
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task An_upload_created_long_ago_but_written_recently_is_not_swept()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();

        // The staleness authority is UpdatedAt, not CreatedAt: a legitimate 40-minute upload of a huge artifact
        // must not be called abandoned because it started long ago (the last write was a moment ago).
        var artifactId = await host.CreateArtifactAsync("dist", ArtifactKind.Directory);
        var bytes = Encoding.UTF8.GetBytes("a");
        using (var uploaded = await host.UploadFileRawAsync(
            artifactId,
            "a.txt",
            bytes,
            Convert.ToHexStringLower(SHA256.HashData(bytes))))
        {
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        }

        await host.AgePendingArtifactsAsync(createdAge: TimeSpan.FromMinutes(45), writtenAge: TimeSpan.FromSeconds(2));

        var sweep = await host.SweepAsync();

        Assert.Equal(0, sweep.IncompleteArtifacts);
        var artifact = Assert.Single(await host.ReadArtifactsAsync());
        Assert.Equal(ArtifactStatus.Pending, artifact.Status);
        Assert.False(token.IsCancellationRequested);
    }

    [Fact(Timeout = 120 * 1000)]
    public async Task Deleting_a_run_removes_its_artifact_rows_and_the_sweeper_removes_its_bytes()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();
        await host.WriteRepositoryTextAsync("out/report.txt", "report");
        await host.PublishAsync("out/report.txt", token);

        var artifact = Assert.Single(await host.ReadArtifactsAsync());
        var artifactDirectory = Path.Combine(
            host.ArtifactRoot,
            artifact.OrganizationId.ToString("D"),
            artifact.RunId.ToString("D"),
            artifact.Id.ToString("D"));
        Assert.True(Directory.Exists(artifactDirectory), "the store directory should exist before the delete");

        // The run delete (the project/organization cascade uses the same foreign key): the rows go with the
        // run, and the sweeper's best-effort pass removes the bytes they described (decision 5).
        await host.Host.InSystemScopeAsync(async context =>
        {
            context.BuildRuns.Remove((await context.SystemQueryAsync<Zenkins.Server.Data.Entities.BuildRun>(
                run => run.Id == host.RunId)).Single());
            await context.SaveChangesAsync(token);
            return true;
        });

        Assert.Empty(await host.ReadArtifactsAsync());

        var sweep = await host.SweepAsync();
        Assert.Equal(1, sweep.RemovedDirectories);
        Assert.False(Directory.Exists(artifactDirectory), "the orphaned store directory should be gone");
    }

    /// <summary>Every message in the exception chain, so a wrapped startup failure is still assertable.</summary>
    private static string Describe(Exception failure)
    {
        var messages = new List<string>();
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }

    private static byte[] DeterministicBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
