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
/// the whole process, so another test's allocations would be attributed to the transfer. The zip temp-scan test
/// used to need it too; it no longer does, because it now points the process's temp directory at a directory of
/// its own instead of scanning the shared one. Do not move a test out of this collection without checking that
/// it does not assert on the process-wide heap or on a process-wide environment variable.
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

    /// <summary>The directory artifact: eight 4 MiB files, so the archive holds 32 MiB before compression.</summary>
    private const int DirectoryPartBytes = 4 * 1024 * 1024;

    private const int DirectoryPartCount = 8;

    private const long DirectoryArtifactBytes = (long)DirectoryPartBytes * DirectoryPartCount;

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

        for (var i = 0; i < DirectoryPartCount; i++)
        {
            var path = host.RepositoryPath($"dist/part{i}.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await WriteLargeFileAsync(path, DirectoryPartBytes, token, seed: i);
        }

        await host.PublishAsync("dist", token);
        var artifact = Assert.Single(await host.ReadArtifactsAsync());
        Assert.Equal(DirectoryPartCount, artifact.FileCount);

        var filesBefore = host.StoreFiles();
        var workFilesBefore = SnapshotFiles(host.Work.RootPath);

        // The observation's scope: the process's temp directory is moved to a directory this fact owns for
        // exactly as long as the download runs, so "a temporary archive appeared" is observed over the only
        // place a temp archive could be written rather than over a root the whole suite shares.
        using var scopedTemp = new ScopedTempRoot();
        var tempBefore = SnapshotDirectory(scopedTemp.Root);

        // Fail closed, before the request: if the redirection had not taken effect, the snapshot below would be
        // watching a directory no temp archive can ever be written to, and this fact would pass whatever the
        // download did. Checked again while the body is being produced, below.
        Assert.Equal(scopedTemp.Root, TempPathNow());

        var telemetry = host.Telemetry;
        telemetry.Reset();

        // The whole archive is held by the test client (so it can be inspected) while the *server* streams it:
        // the server-side metrics are what is asserted, and an archive on disk anywhere under the scoped temp
        // root would show up as a new entry in the snapshot.
        var archiveBytes = new MemoryStream();
        using (var download = await host.DownloadStreamingAsync(artifact.Id))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);

            // Fail closed: if the temp redirection had not taken effect, the snapshot below would be watching a
            // directory no temp archive can ever be written to, and this fact would pass whatever the download
            // did. Asserted here, while the body is being produced, not once at setup.
            Assert.Equal(scopedTemp.Root, TempPathNow());

            await using var body = await download.Content.ReadAsStreamAsync(token);
            var buffer = new byte[64 * 1024];
            var chunks = 0;
            long received = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, token)) > 0)
            {
                archiveBytes.Write(buffer, 0, read);
                received += read;
                if (++chunks == 64)
                {
                    // A snapshot taken while the download is in flight: a temp archive would exist now.
                    Assert.Equal(tempBefore, SnapshotDirectory(scopedTemp.Root));

                    // ... and the archive is produced as the client consumes it. This bound is supplementary: a
                    // buffered implementation that reuses this instrumented copy loop has already had all 32 MiB
                    // counted by now, which fails here. One that copies with its own loop, or via
                    // ZipFile.CreateFromDirectory, leaves BytesSent at zero and is NOT caught by this bound - the
                    // scoped snapshot above remains the only guard for that shape.
                    Assert.True(
                        telemetry.BytesSent < DirectoryArtifactBytes,
                        $"the server had already copied its whole {DirectoryArtifactBytes}-byte archive after "
                        + $"{received} bytes reached the client - that is a materialised archive, not a stream");
                }
            }

            // The zip carries headers on top of the stored bytes, so the body is at least the artifact's size.
            // (The exact size is checked through the entries below, not through a compressed-size guess.)
            Assert.True(
                received >= DirectoryArtifactBytes,
                $"the body carried only {received} bytes of a {DirectoryArtifactBytes}-byte artifact");
        }

        Assert.Equal(tempBefore, SnapshotDirectory(scopedTemp.Root));

        // The zip is what the artifact holds: eight entries at their relative paths, nothing absolute, no '..'.
        archiveBytes.Position = 0;
        using (var zip = new ZipArchive(archiveBytes, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.Equal(DirectoryPartCount, zip.Entries.Count);
            Assert.Equal(
                [.. Enumerable.Range(0, DirectoryPartCount).Select(index => $"part{index}.bin").OrderBy(name => name, StringComparer.Ordinal)],
                zip.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal));
            Assert.All(zip.Entries, entry => Assert.Equal(DirectoryPartBytes, entry.Length));
        }

        // The store grew by nothing and the run's work tree gained nothing: the zip was never materialised next
        // to the artifact it describes, whether in the store or in the checkout the artifact came from.
        Assert.Equal(filesBefore, host.StoreFiles());
        Assert.DoesNotContain(host.StoreFiles(), path => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(workFilesBefore, SnapshotFiles(host.Work.RootPath));

        Assert.True(
            telemetry.BytesSent >= DirectoryArtifactBytes,
            $"the zip delivered only {telemetry.BytesSent} bytes of a {DirectoryArtifactBytes}-byte artifact");
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
    /// The observation's scope, as an object with a lifetime: a directory this fact creates, with the process's
    /// temp directory pointed at it for as long as it lives. <see cref="Dispose"/> restores the previous variables
    /// and removes the directory.
    /// <para>
    /// <b>Why moving the temp directory, rather than scanning the shared one more narrowly.</b> The property is
    /// "no temporary archive exists anywhere", so the observation has to cover every place the product would
    /// write one - and the product asks the OS where that is. Answering with a directory this fact owns keeps the
    /// property (an archive of any name and any size still appears as a new entry in
    /// <see cref="SnapshotDirectory"/>) while removing what made it flaky: the path is unique to this fact, and
    /// this collection does not run in parallel, so no other test in the process is sharing the process temp
    /// directory while the scope is alive - the same isolation the heap measurement already requires, so nothing
    /// new is serialised. That isolation is the collection's contract, not something this fact widens: it does
    /// not need the shared temp root to stay quiet, it only needs the process temp answer, which is its own. The
    /// answer is not cached - the runtime re-reads <c>TMPDIR</c>/<c>TMP</c>/<c>TEMP</c> on every
    /// <see cref="Path.GetTempPath"/> call - so a path captured before this object existed is not a way around
    /// the scope, and the fact asserts <see cref="TempPathNow"/> before the request and again while the body is
    /// produced, so a redirection that silently stopped working fails the test instead of quietly emptying the
    /// assertion.
    /// </para>
    /// </summary>
    private sealed class ScopedTempRoot : IDisposable
    {
        /// <summary>The variables <see cref="Path.GetTempPath"/> reads: <c>TMPDIR</c> on Unix, <c>TMP</c>/<c>TEMP</c> on Windows.</summary>
        private static readonly string[] Variables = ["TMPDIR", "TMP", "TEMP"];

        private readonly string?[] _restore;

        public ScopedTempRoot()
        {
            // Resolve the OS temp directory *first*, then create a unique directory inside it.
            Root = Path.Combine(
                Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                $"zenkins-f016-zip-scope-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);

            _restore =
            [
                Environment.GetEnvironmentVariable(Variables[0]),
                Environment.GetEnvironmentVariable(Variables[1]),
                Environment.GetEnvironmentVariable(Variables[2]),
            ];

            foreach (var variable in Variables)
            {
                Environment.SetEnvironmentVariable(variable, Root);
            }
        }

        /// <summary>The absolute directory the process now treats as its temp directory.</summary>
        public string Root { get; }

        public void Dispose()
        {
            for (var index = 0; index < Variables.Length; index++)
            {
                Environment.SetEnvironmentVariable(Variables[index], _restore[index]);
            }

            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leaked reader must not mask the real failure; the directory is in the temp root.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The process's temp directory right now, resolved and without a trailing separator.</summary>
    private static string TempPathNow() =>
        Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>
    /// Every entry under the scoped temp root (recursively), as <c>relative-path|length</c>, with directories and
    /// links as <c>|-1</c>. Comparing three snapshots (before, during and after a download) proves no temporary
    /// archive appeared - <b>without</b> a name filter and <b>without</b> a size filter, because the root is this
    /// fact's own: there is no foreign churn to exclude, and an archive that was named something other than
    /// <c>*.zip</c>, or written empty as a first step, is caught too. (Over the shared OS temp root both filters
    /// were necessary - other processes' sockets, zero-length markers and other tests' scratch zips - and each of
    /// them narrowed what could be caught.)
    /// </summary>
    private static string[] SnapshotDirectory(string root)
    {
        var absolute = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var entries = new List<string>();

        // IgnoreInaccessible keeps a system-owned child from failing the snapshot; a directory this fact creates
        // is always enumerable, so nothing it writes is missed.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        foreach (var entry in new DirectoryInfo(absolute).EnumerateFileSystemInfos("*", options))
        {
            try
            {
                var relative = Path.GetRelativePath(absolute, entry.FullName)
                    .Replace(Path.DirectorySeparatorChar, '/');

                if (entry is DirectoryInfo || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    entries.Add(relative + "|-1");
                    continue;
                }

                var file = (FileInfo)entry;
                if (!file.Exists)
                {
                    // Not a regular file at all: nothing a download can materialise.
                    continue;
                }

                entries.Add(relative + "|" + file.Length);
            }
            catch (IOException)
            {
                // A file removed between enumeration and stat: not this download's business.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        entries.Sort(StringComparer.Ordinal);
        return [.. entries];
    }

    /// <summary>Every file under <paramref name="root"/>, as <c>/</c>-separated relative paths (no sizes).</summary>
    private static string[] SnapshotFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(path => path, StringComparer.Ordinal),
        ];
    }

    // ------------------------------------------------------------------
    // The temp-scan harness itself (review m10)
    // ------------------------------------------------------------------

    [Fact]
    public void The_scoped_scan_catches_a_temp_archive_written_the_way_the_product_would_write_one()
    {
        // Red proof for the scan: a new non-empty file written through Path.GetTempPath() - exactly how an
        // implementation that zips to a temporary archive would place it - must change the snapshot of the
        // scope. Without this, the scope could drift into watching a directory nothing writes to and the zip
        // fact would become an assertion that cannot fail.
        using var scoped = new ScopedTempRoot();
        var before = SnapshotDirectory(scoped.Root);

        var name = $"zenkins-f016-selftest-{Guid.NewGuid():N}.zip";
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), name), [0x50, 0x4B, 0x03, 0x04]);

        var after = SnapshotDirectory(scoped.Root);
        Assert.NotEqual(before, after);
        Assert.Contains(after, entry => entry.Contains(name, StringComparison.Ordinal));
    }

    [Fact]
    public void A_zero_length_entry_in_the_scope_is_part_of_the_snapshot()
    {
        // Over the shared temp root the scan had to skip zero-length entries: other applications keep AF_UNIX
        // sockets there (observed: "Sublime Text.<pid>.<gen>.socket"), which .NET reports as an existing "file"
        // of length 0, and their churn is nobody's download. Nothing but this fact writes to the scope, so the
        // skip is gone and an empty entry is a difference like any other - an archive is never empty, so this
        // catches more than the old filter did, not less.
        using var scoped = new ScopedTempRoot();

        var name = $"zenkins-f016-selftest-{Guid.NewGuid():N}.entry";
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), name), []);

        Assert.Contains(SnapshotDirectory(scoped.Root), entry => entry.Contains(name, StringComparison.Ordinal));
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
