using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Xunit;
using Zenkins.Server.Artifacts;
using Zenkins.Shared.Artifacts;

namespace Zenkins.Tests.Artifacts;

/// <summary>
/// The collection the streaming tests run in. xUnit's <c>DisableParallelization</c> makes this collection run
/// alone, which the heap measurement needs: <see cref="ArtifactTransferTelemetry.PeakManagedDeltaBytes"/> samples
/// the whole process, so another test's allocations would otherwise be attributed to the transfer.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ArtifactStreamingCollection
{
    public const string Name = "ArtifactsStreaming";
}

/// <summary>
/// Card F-016, TC-004 (2) and the review checklist's "streaming must be <b>measured</b>": a fixture far larger
/// than any buffer crosses the upload and the download, and the server's own counters have to <b>discriminate</b>
/// streaming from buffering.
/// <para>
/// <b>Why the metrics can fail.</b> <see cref="ArtifactTransferTelemetry.PeakChunkBytes"/> is the largest chunk a
/// copy loop actually read, and <see cref="ArtifactTransferTelemetry.PeakManagedDeltaBytes"/> is the process
/// heap's high-water mark during the transfer. A copy that reads the artifact into a <c>MemoryStream</c> before
/// writing it leaves the chunk size at 64 KiB but grows the heap by the artifact's size, so the second metric is
/// the one that catches it - and <see cref="The_heap_metric_distinguishes_a_streamed_copy_from_a_buffered_one"/>
/// runs both shapes through the same measurement to prove that it does.
/// </para>
/// </summary>
[Trait("Category", "Artifacts")]
[Collection(ArtifactStreamingCollection.Name)]
public sealed class ArtifactStreamingTests
{
    /// <summary>48 MB: big enough that a whole-file buffer is unmistakable, small enough to be quick.</summary>
    private const int FixtureBytes = 48 * 1024 * 1024;

    /// <summary>The server's copy buffer is 64 KiB; a chunk may not exceed it (a buffered copy would).</summary>
    private const long MaxChunkBytes = 64 * 1024;

    /// <summary>
    /// The bound a streamed transfer must stay under. Deliberately half the fixture: a buffered implementation
    /// would have to allocate the whole 48 MB, while a chunked copy allocates kilobytes per chunk.
    /// </summary>
    private const long MaxManagedDelta = FixtureBytes / 2;

    [Fact(Timeout = 300 * 1000)]
    public async Task A_48_megabyte_upload_and_download_stream_with_a_bounded_heap()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();

        var path = host.RepositoryPath("bin/big.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var expectedHash = await WriteLargeFileAsync(path, FixtureBytes, token);

        var telemetry = host.Telemetry;
        telemetry.Reset();

        // ----- Upload: 48 MB through the request body, hashed and written chunk by chunk -----
        await host.PublishAsync("bin/big.bin", token);

        var artifact = Assert.Single(await host.ReadArtifactsAsync());
        Assert.Equal(ArtifactStatus.Ready, artifact.Status);
        Assert.Equal(FixtureBytes, artifact.TotalBytes);

        Assert.True(
            telemetry.BytesReceived >= FixtureBytes,
            $"the server received {telemetry.BytesReceived} bytes, expected at least {FixtureBytes}");
        Assert.True(
            telemetry.PeakChunkBytes is > 0 and <= MaxChunkBytes,
            $"the upload's largest single read was {telemetry.PeakChunkBytes} bytes (bound {MaxChunkBytes})");
        Assert.True(
            telemetry.PeakManagedDeltaBytes < MaxManagedDelta,
            $"the upload grew the managed heap by {telemetry.PeakManagedDeltaBytes} bytes - that is a buffered copy, not a stream");

        // ----- Download: streamed by the client so its own buffering cannot inflate the measurement -----
        telemetry.Reset();
        using var download = await host.DownloadStreamingAsync(artifact.Id);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var body = await download.Content.ReadAsStreamAsync(token);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, token)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            total += read;
        }

        Assert.Equal(FixtureBytes, total);
        Assert.Equal(expectedHash, Convert.ToHexStringLower(hash.GetHashAndReset()));

        Assert.True(
            telemetry.BytesSent >= FixtureBytes,
            $"the server streamed {telemetry.BytesSent} bytes, expected at least {FixtureBytes}");
        Assert.True(
            telemetry.PeakChunkBytes is > 0 and <= MaxChunkBytes,
            $"the download's largest single read was {telemetry.PeakChunkBytes} bytes (bound {MaxChunkBytes})");
        Assert.True(
            telemetry.PeakManagedDeltaBytes < MaxManagedDelta,
            $"the download grew the managed heap by {telemetry.PeakManagedDeltaBytes} bytes - that is a buffered copy, not a stream");
    }

    [Fact(Timeout = 300 * 1000)]
    public async Task A_directory_download_is_zipped_on_the_fly_and_writes_no_temp_archive()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = await ArtifactsTestHost.CreateAsync();

        for (var i = 0; i < 8; i++)
        {
            var path = host.RepositoryPath($"dist/part{i}.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await WriteLargeFileAsync(path, 4 * 1024 * 1024, token, seed: i);
        }

        await host.PublishAsync("dist", token);
        var artifact = Assert.Single(await host.ReadArtifactsAsync());
        Assert.Equal(8, artifact.FileCount);

        var filesBefore = host.StoreFiles();
        var tempBefore = SnapshotTempDirectory();
        var telemetry = host.Telemetry;
        telemetry.Reset();

        // The whole archive is held by the test client (so it can be inspected) while the *server* streams it:
        // the server-side metrics are what is asserted, and an archive on disk anywhere under the OS temp root
        // would show up as a new entry in the snapshot.
        var archiveBytes = new MemoryStream();
        string[] tempDuring;
        using (var download = await host.DownloadStreamingAsync(artifact.Id))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);

            await using var body = await download.Content.ReadAsStreamAsync(token);
            var buffer = new byte[64 * 1024];
            var chunks = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, token)) > 0)
            {
                archiveBytes.Write(buffer, 0, read);
                if (++chunks == 64)
                {
                    // A snapshot taken while the download is in flight: a temp archive would exist now.
                    tempDuring = SnapshotTempDirectory();
                    Assert.Equal(tempBefore, tempDuring);
                }
            }

            tempDuring = SnapshotTempDirectory();
        }

        var tempAfter = SnapshotTempDirectory();
        Assert.Equal(tempBefore, tempDuring);
        Assert.Equal(tempBefore, tempAfter);

        // The zip is what the artifact holds: eight entries at their relative paths, nothing absolute, no '..'.
        archiveBytes.Position = 0;
        using (var zip = new ZipArchive(archiveBytes, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.Equal(8, zip.Entries.Count);
            Assert.Equal(
                [.. Enumerable.Range(0, 8).Select(index => $"part{index}.bin").OrderBy(name => name, StringComparer.Ordinal)],
                zip.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal));
            Assert.All(zip.Entries, entry => Assert.Equal(4 * 1024 * 1024, entry.Length));
        }

        // The store grew by nothing: the zip was never materialised next to the artifact it describes.
        Assert.Equal(filesBefore, host.StoreFiles());
        Assert.DoesNotContain(host.StoreFiles(), path => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        Assert.True(
            telemetry.BytesSent >= 32L * 1024 * 1024,
            $"the zip delivered only {telemetry.BytesSent} bytes of a 32 MB artifact");
        Assert.True(
            telemetry.PeakChunkBytes is > 0 and <= MaxChunkBytes,
            $"the zip's largest single read was {telemetry.PeakChunkBytes} bytes (bound {MaxChunkBytes})");
    }

    /// <summary>
    /// The mutation evidence for the heap metric: the same measurement, run over a streamed copy and over the
    /// "read it all into a <see cref="MemoryStream"/>, then write it" shape the reviewer described. The streamed
    /// copy stays under the bound the production assertions use; the buffered one exceeds it. Swapping the store's
    /// copy loop for the buffered shape therefore turns
    /// <see cref="A_48_megabyte_upload_and_download_stream_with_a_bounded_heap"/> red - the metric is not a
    /// tautology.
    /// </summary>
    [Fact(Timeout = 300 * 1000)]
    public async Task The_heap_metric_distinguishes_a_streamed_copy_from_a_buffered_one()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = Path.Combine(Path.GetTempPath(), $"zenkins-f016-metric-{Guid.NewGuid():N}.bin");
        try
        {
            await WriteLargeFileAsync(fixture, FixtureBytes, token);

            var streamed = await MeasureAsync(fixture, bufferWholeFile: false, token);
            var buffered = await MeasureAsync(fixture, bufferWholeFile: true, token);

            Assert.True(
                streamed.ManagedDelta < MaxManagedDelta,
                $"the streamed copy grew the heap by {streamed.ManagedDelta} bytes (bound {MaxManagedDelta})");
            Assert.True(
                buffered.ManagedDelta > MaxManagedDelta,
                $"the buffered copy grew the heap by only {buffered.ManagedDelta} bytes, so the metric would not "
                + "catch a whole-file buffer");

            // Both shapes read 64 KiB at a time, so the chunk metric alone cannot see the difference - which is
            // exactly why the heap metric exists.
            Assert.True(streamed.PeakChunk <= MaxChunkBytes);
            Assert.True(buffered.PeakChunk <= MaxChunkBytes);
            Assert.Equal(FixtureBytes, streamed.Bytes);
            Assert.Equal(FixtureBytes, buffered.Bytes);
        }
        finally
        {
            if (File.Exists(fixture))
            {
                File.Delete(fixture);
            }
        }
    }

    /// <summary>The two copy shapes, measured through the same production telemetry.</summary>
    private static async Task<(long ManagedDelta, long PeakChunk, long Bytes)> MeasureAsync(
        string path,
        bool bufferWholeFile,
        CancellationToken cancellationToken)
    {
        var telemetry = new ArtifactTransferTelemetry();
        telemetry.Reset();

        var buffer = new byte[64 * 1024];
        MemoryStream? buffered = bufferWholeFile ? new MemoryStream() : null;
        Stream sink = buffered ?? Stream.Null;
        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var total = 0;
        int read;
        while ((read = await file.ReadAsync(buffer, cancellationToken)) > 0)
        {
            // The production copy loop's measurement, unchanged: one sample per chunk read.
            telemetry.ObserveChunk(read);
            await sink.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
        }

        return (telemetry.PeakManagedDeltaBytes, telemetry.PeakChunkBytes, total);
    }

    /// <summary>
    /// Every entry under the OS temp directory (recursively), as <c>relative-path|length</c>. Comparing three
    /// snapshots (before, during and after a download) proves no temporary archive appeared anywhere - without
    /// filtering by name or by run id, so a differently named temp file would still be caught.
    /// </summary>
    private static string[] SnapshotTempDirectory()
    {
        var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var entries = new List<string>();

        // IgnoreInaccessible keeps a system-owned child (macOS's TemporaryItems, a root-only directory) from
        // failing the snapshot; a directory the test can write is always enumerable, so nothing it creates is
        // missed.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", options))
        {
            try
            {
                var info = new FileInfo(path);
                var length = info.LinkTarget is null && info.Exists ? info.Length : -1;
                entries.Add(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/') + "|" + length);
            }
            catch (IOException)
            {
                // A file another process removed between enumeration and stat: not this download's business.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        entries.Sort(StringComparer.Ordinal);
        return [.. entries];
    }

    /// <summary>Writes <paramref name="bytes"/> of deterministic content in 1 MiB chunks and returns its SHA-256.</summary>
    private static async Task<string> WriteLargeFileAsync(
        string path,
        int bytes,
        CancellationToken cancellationToken,
        int seed = 42)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunk = new byte[1024 * 1024];
        new Random(seed).NextBytes(chunk);

        await using var file = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous);

        var remaining = bytes;
        while (remaining > 0)
        {
            var length = Math.Min(chunk.Length, remaining);
            // Vary each block so a byte-order bug cannot hide behind repetition.
            chunk[0] = (byte)(remaining & 0xFF);
            hash.AppendData(chunk, 0, length);
            await file.WriteAsync(chunk.AsMemory(0, length), cancellationToken);
            remaining -= length;
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
