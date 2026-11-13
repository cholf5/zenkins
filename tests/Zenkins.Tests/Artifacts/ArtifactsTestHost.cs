using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Artifacts;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.Artifacts;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Runs;
using Zenkins.Tests.Workers;
using Zenkins.Worker;
using Zenkins.Worker.Artifacts;

namespace Zenkins.Tests.Artifacts;

/// <summary>
/// F-016's harness: the <b>real</b> server (through <see cref="RunTestHost"/>, so the artifact endpoints,
/// the store and the tenant filter are the product's) plus the <b>real</b> worker publisher
/// (<see cref="ArtifactPublisher"/> against a temporary work directory). Every test drives the product's own
/// code; nothing here re-implements a rule the product owns.
/// <para>
/// <b>The store root follows the temporary database.</b> <c>Artifacts:RootPath</c> is set to a directory next
/// to this host's temp SQLite file, exactly like the Data Protection key ring is pointed at a temp directory
/// (never <c>src/Server/App_Data</c>), so a test run cannot pollute the repository. <see cref="Dispose"/>
/// removes it.
/// </para>
/// <para>
/// <b>Caps are configuration, not code.</b> A test that exercises a cap sets it through
/// <c>Artifacts:Max*</c> before the host starts, so the product's own reading path and its own messages are
/// what the assertions see.
/// </para>
/// </summary>
internal sealed class ArtifactsTestHost : IDisposable
{
    private readonly string _workRoot;

    private ArtifactsTestHost(RunTestHost host, string artifactRoot, string workRoot)
    {
        Host = host;
        ArtifactRoot = artifactRoot;
        _workRoot = workRoot;
    }

    /// <summary>The server host (real endpoints, real database, real DI graph).</summary>
    public RunTestHost Host { get; }

    /// <summary>The absolute artifact store root this host was configured with.</summary>
    public string ArtifactRoot { get; }

    public Guid OrganizationId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid RunId { get; private set; }

    public Guid WorkerId { get; private set; }

    public string WorkerToken { get; private set; } = string.Empty;

    /// <summary>The run's work directory (its <c>repository/</c> is the only artifact source root).</summary>
    public RunWorkDirectory Work { get; private set; } = null!;

    /// <summary>A client with the owner's cookie jar (downloads and pages).</summary>
    public HttpClient OwnerClient { get; private set; } = null!;

    /// <summary>The worker API client the harness's publisher uses (a fresh client per host).</summary>
    public HttpClient WorkerClient { get; private set; } = null!;

    /// <summary>Creates a host with optional cap overrides, a claimed+accepted run and a prepared work tree.</summary>
    public static async Task<ArtifactsTestHost> CreateAsync(IDictionary<string, string?>? settings = null)
    {
        // The artifact store root and the work root are both under the process temp directory: the same
        // discipline the Data Protection key ring follows in the other hosts, so a test run cannot write into
        // the repository (and `git status --ignored` stays clean).
        var artifactRoot = Path.Combine(Path.GetTempPath(), $"zenkins-f016-artifacts-{Guid.NewGuid():N}");
        var workRoot = Path.Combine(Path.GetTempPath(), $"zenkins-f016-work-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);

        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Artifacts:RootPath"] = artifactRoot,
            // The sweeper must never fire on its own during a test; every sweep is an explicit tick.
            ["Artifacts:SweepIntervalSeconds"] = "36000",
            ["Artifacts:PendingTimeoutMinutes"] = "30",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            configuration[key] = value;
        }

        var host = new ArtifactsTestHost(
            await RunTestHost.CreateAsync(builder => builder.AddInMemoryCollection(configuration)),
            artifactRoot,
            workRoot);
        try
        {
            host.OwnerClient = await host.Host.Inner.SignInOwnerAsync();
            await host.StartRunAsync();
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    /// <summary>Truncates caps to something a test can reach without writing gigabytes.</summary>
    public static Dictionary<string, string?> Caps(long maxFileBytes, long maxArtifactBytes, int maxFiles) =>
        new(StringComparer.Ordinal)
        {
            ["Artifacts:MaxFileBytes"] = maxFileBytes.ToString(),
            ["Artifacts:MaxArtifactBytes"] = maxArtifactBytes.ToString(),
            ["Artifacts:MaxFiles"] = maxFiles.ToString(),
        };

    private async Task StartRunAsync()
    {
        OrganizationId = await Host.OrganizationIdAsync();
        ProjectId = await Host.SeedProjectAsync(
            $"artifacts-project-{Guid.NewGuid():N}",
            "https://example.test/artifacts.git");
        (WorkerToken, WorkerId) = await Host.RegisterWorkerAsync(name: $"artifacts-agent-{Guid.NewGuid():N}");

        RunId = await RunTestHost.TriggerRunAsync(OwnerClient, ProjectId);

        WorkerClient = Host.Host.CreateClient();
        using var claimed = await RunTestHost.ClaimAsync(WorkerClient, WorkerToken);
        Assert.Equal(RunId, await RunTestHost.ReadClaimedRunIdAsync(claimed));

        using var accepted = await RunTestHost.AcceptAsync(WorkerClient, WorkerToken, RunId);
        Assert.Equal(System.Net.HttpStatusCode.OK, accepted.StatusCode);

        Work = new WorkDirectoryService(_workRoot).Prepare(RunId);
    }

    // ------------------------------------------------------------------
    // The source tree (the checkout) the publisher reads
    // ------------------------------------------------------------------

    /// <summary>Absolute path inside the run's checkout for a repository-relative path.</summary>
    public string RepositoryPath(string relativePath) => Path.Combine(
        Work.RepositoryPath,
        relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Writes a file into the checkout (creating its directories).</summary>
    public async Task<string> WriteRepositoryFileAsync(string relativePath, byte[] bytes)
    {
        var path = RepositoryPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        return path;
    }

    /// <summary>Writes a text file into the checkout.</summary>
    public Task<string> WriteRepositoryTextAsync(string relativePath, string text) =>
        WriteRepositoryFileAsync(relativePath, Encoding.UTF8.GetBytes(text));

    /// <summary>Writes a file into <c>work/{run-id}/artifacts/</c> - deliberately <b>not</b> a source root.</summary>
    public async Task<string> WriteRunArtifactsDirectoryFileAsync(string relativePath, byte[] bytes)
    {
        var path = Path.Combine(
            Work.ArtifactsPath,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        return path;
    }

    // ------------------------------------------------------------------
    // The product's publisher (the real worker half)
    // ------------------------------------------------------------------

    /// <summary>A publisher wired exactly as the run loop wires it (same client, same work tree, same caps).</summary>
    public ArtifactPublisher CreatePublisher(Action<string>? log = null, ArtifactLimits? limits = null) =>
        new(
            new ArtifactUploadClient(new WorkerApiClient(WorkerClient), WorkerToken),
            RunId,
            Work.RepositoryPath,
            Work.RootPath,
            limits ?? ArtifactLimits.FromConfiguration(key => Host.Host.Services
                .GetRequiredService<IConfiguration>()[key]),
            log ?? (_ => { }));

    /// <summary>
    /// Publishes one declared path with the real publisher and the run's (test) cancellation token. Pass
    /// <paramref name="limits"/> to separate the worker's pre-check from the server's authoritative one: the
    /// cap tests that target the server pass <see cref="ArtifactLimits.Defaults"/> so the worker never refuses
    /// first.
    /// </summary>
    public Task PublishAsync(
        string declaredPath,
        CancellationToken cancellationToken = default,
        ArtifactLimits? limits = null) =>
        CreatePublisher(limits: limits).PublishAsync(
            declaredPath,
            cancellationToken == default ? TestContext.Current.CancellationToken : cancellationToken);

    // ------------------------------------------------------------------
    // The worker protocol, called by hand (the crafted-upload tests)
    // ------------------------------------------------------------------

    /// <summary>Reserves an artifact row through the real endpoint and returns its id.</summary>
    public async Task<Guid> CreateArtifactAsync(string name, string kind = ArtifactKind.File)
    {
        using var response = await SendArtifactAsync(
            HttpMethod.Post,
            $"/api/workers/runs/{RunId}/artifacts",
            new ArtifactCreateRequest(name, kind));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ArtifactCreateResponse>();
        return body!.ArtifactId;
    }

    /// <summary>
    /// Posts one file with an explicit (already encoded) query string - the seam the traversal tests use,
    /// because it lets them claim a relative path the publisher would never send.
    /// </summary>
    public async Task<HttpResponseMessage> UploadFileRawAsync(
        Guid artifactId,
        string pathInQuery,
        byte[] bytes,
        string? sha256 = null)
    {
        var query = $"?path={pathInQuery}";
        if (sha256 is not null)
        {
            query += $"&sha256={sha256}";
        }

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/workers/runs/{RunId}/artifacts/{artifactId}/files{query}")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WorkerToken);
        return await WorkerClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Finalizes an artifact through the real endpoint.</summary>
    public async Task<HttpResponseMessage> FinalizeArtifactAsync(Guid artifactId, params ArtifactFileEntry[] files)
    {
        return await SendArtifactAsync(
            HttpMethod.Post,
            $"/api/workers/runs/{RunId}/artifacts/{artifactId}/finalize",
            new ArtifactFinalizeRequest(files));
    }

    private async Task<HttpResponseMessage> SendArtifactAsync<T>(
        HttpMethod method,
        string path,
        T payload)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WorkerToken);
        return await WorkerClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // ------------------------------------------------------------------
    // Downloads and reads
    // ------------------------------------------------------------------

    /// <summary>
    /// Requests one artifact download and returns as soon as the headers arrive, so the caller streams the body.
    /// The default <see cref="HttpClient.GetAsync(string, CancellationToken)"/> buffers the whole content in
    /// memory first, which would both hide a download bug and pollute the heap metric.
    /// </summary>
    public Task<HttpResponseMessage> DownloadStreamingAsync(Guid artifactId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/runs/{RunId}/artifacts/{artifactId}/download");
        return OwnerClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);
    }

    /// <summary>Requests one artifact download as the owner (the product's cookie-authenticated route).</summary>
    public Task<HttpResponseMessage> DownloadAsync(Guid artifactId, HttpClient? client = null) =>
        (client ?? OwnerClient).GetAsync(
            $"/runs/{RunId}/artifacts/{artifactId}/download",
            TestContext.Current.CancellationToken);

    /// <summary>Reads one artifact row across tenants (the assertion side, never the filtered view).</summary>
    public Task<Artifact?> ReadArtifactAsync(Guid artifactId) =>
        Host.InSystemScopeAsync(async context =>
            (await context.SystemQueryAsync<Artifact>(artifact => artifact.Id == artifactId))
                .SingleOrDefault());

    /// <summary>Reads every artifact row of the run across tenants.</summary>
    public Task<List<Artifact>> ReadArtifactsAsync() =>
        Host.InSystemScopeAsync(context =>
            context.SystemQueryAsync<Artifact>(artifact => artifact.RunId == RunId));

    /// <summary>The server's transfer counters (the streaming proof).</summary>
    public ArtifactTransferTelemetry Telemetry => Host.Host.Services.GetRequiredService<ArtifactTransferTelemetry>();

    /// <summary>Runs one sweeper tick through the product's own hosted service.</summary>
    public Task<ArtifactSweepResult> SweepAsync() =>
        Host.Host.InScopeAsync(services =>
            services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                .OfType<ArtifactSweeper>()
                .Single()
                .SweepOnceAsync(TestContext.Current.CancellationToken));

    /// <summary>
    /// Ages every <c>Pending</c> artifact row of this run (both timestamps) so the sweeper treats it as
    /// abandoned - the deterministic stand-in for waiting out <see cref="ArtifactOptions.PendingTimeout"/>.
    /// </summary>
    public Task AgePendingArtifactsAsync(TimeSpan age) =>
        AgePendingArtifactsAsync(createdAge: age, writtenAge: age);

    /// <summary>
    /// Ages the two timestamps independently, which is what separates "created long ago" from "nobody has written
    /// for the whole window": the sweeper's staleness authority is <c>UpdatedAt</c>.
    /// </summary>
    public Task AgePendingArtifactsAsync(TimeSpan createdAge, TimeSpan? writtenAge) =>
        Host.InSystemScopeAsync(async context =>
        {
            var now = DateTimeOffset.UtcNow;
            var created = now - createdAge;
            var written = now - (writtenAge ?? createdAge);

            // A guarded UPDATE, not entity edits: the context's single timestamp routine rewrites UpdatedAt on
            // every save (deliberately), so the only way to place a row's timestamps in the past is to bypass it.
            await context.Set<Artifact>()
                .IgnoreQueryFilters()
                .Where(artifact => artifact.RunId == RunId && artifact.Status == ArtifactStatus.Pending)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(artifact => artifact.CreatedAt, created)
                    .SetProperty(artifact => artifact.UpdatedAt, written));
            return true;
        });

    /// <summary>The run page's HTML.</summary>
    public Task<string> ReadRunPageAsync() => AuthTestHost.GetStringAsync(OwnerClient, $"/runs/{RunId}");

    /// <summary>
    /// Every file under the artifact store root, as <c>/</c>-separated relative paths. Used to prove what did
    /// (and did not) land on disk - including that a refused upload wrote nothing.
    /// </summary>
    public IReadOnlyList<string> StoreFiles()
    {
        if (!Directory.Exists(ArtifactRoot))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateFiles(ArtifactRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(ArtifactRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(path => path, StringComparer.Ordinal),
        ];
    }

    public void Dispose()
    {
        WorkerClient?.Dispose();
        OwnerClient.Dispose();
        Host.Dispose();

        foreach (var directory in new[] { ArtifactRoot, _workRoot })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leaked reader must not mask the real failure; the directory is in the temp root.
            }
        }
    }
}
