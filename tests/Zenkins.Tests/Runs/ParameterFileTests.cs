using System.Net;
using System.Text.Json;
using Xunit;
using Zenkins.Server.Projects;
using Zenkins.Server.Runs;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Projects;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Pipelines;
using Zenkins.Tests.Steps;
using Zenkins.Tests.Workers;
using Zenkins.Worker;
using Zenkins.Worker.Pipelines;

namespace Zenkins.Tests.Runs;

/// <summary>
/// Card F-041's <c>file</c> parameter: uploaded with the trigger, recorded as a <b>path</b>, sealed on the run and
/// delivered to the worker over its own run-scoped route - never in the claim, never downloadable.
/// </summary>
[Trait("Category", "Run")]
public sealed class ParameterFileTests
{
    private const string FileName = "LICENSE";
    private const string UploadName = "license.txt";
    private const string FileText = "MIT-license-body";

    private static byte[] Upload => System.Text.Encoding.UTF8.GetBytes(FileText);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_uploaded_file_parameter_is_recorded_as_a_path_and_never_in_the_claim()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("file-param");
        await SaveFileParameterAsync(owner, projectId);

        var runId = await TriggerWithFileAsync(host, owner, projectId);
        var run = await host.ReadRunAsync(runId);

        // 1. The run page's value is the run-relative location, and the bytes are sealed beside the value map.
        var values = JsonSerializer.Deserialize(
            run.ParametersJson!,
            ProjectParameterJsonContext.Default.DictionaryStringString)!;
        Assert.Equal(ProjectParameterLimits.FileLocation(FileName), values[FileName]);
        Assert.NotNull(run.ParameterFilesJson);
        Assert.DoesNotContain(FileText, run.ParametersJson!, StringComparison.Ordinal);
        Assert.DoesNotContain(FileText, run.ParameterFilesJson!, StringComparison.Ordinal);

        // 2. The page shows that location and offers no way to fetch the file - it may be a secret.
        var page = await AuthTestHost.GetStringAsync(owner, $"/runs/{runId}");
        Assert.Contains(ProjectParameterLimits.FileLocation(FileName), page, StringComparison.Ordinal);
        Assert.Contains("not downloadable", page, StringComparison.Ordinal);
        // No link to the parameter-file route (the page's log download is a different, unrelated control).
        Assert.DoesNotContain("parameter-files", page, StringComparison.Ordinal);
        Assert.DoesNotContain(FileText, page, StringComparison.Ordinal);

        // 3. The claim names the parameter and carries no byte of it (card F-040's "names only" rule).
        var (token, _) = await host.RegisterWorkerAsync();
        using var worker = host.Host.CreateClient();
        using var claim = await RunTestHost.ClaimAsync(worker, token);
        var claimBody = await claim.Content.ReadAsStringAsync(CancellationToken);
        var payload = JsonSerializer.Deserialize(claimBody, WorkerJsonContext.Default.WorkerClaimResponse);

        Assert.NotNull(payload);
        Assert.Equal(runId, payload!.RunId);
        Assert.Equal([FileName], payload.FileParameters);
        Assert.Equal(ProjectParameterLimits.FileLocation(FileName), payload.Parameters![FileName]);
        Assert.DoesNotContain(FileText, claimBody, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Upload), claimBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_parameter_file_route_serves_the_upload_and_refuses_every_miss_identically()
    {
        using var host = await RunTestHost.CreateAsync();
        var (projectId, owner) = await host.CreateProjectAsync("file-param-route");
        await SaveFileParameterAsync(owner, projectId);
        var runId = await TriggerWithFileAsync(host, owner, projectId);

        var (token, _) = await host.RegisterWorkerAsync();
        using var worker = host.Host.CreateClient();
        using var claim = await RunTestHost.ClaimAsync(worker, token);
        using var accepted = await RunTestHost.AcceptAsync(worker, token, runId);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        // 1. The worker executing the run gets the bytes, raw and uncached.
        var served = await FetchAsync(worker, token, runId, FileName);
        Assert.Equal(HttpStatusCode.OK, served.Status);
        Assert.Equal("no-store", served.CacheControl);
        Assert.Equal(Upload, served.Bytes);

        // 2. Every miss is the same bodyless 404: an unknown name, an unknown run, another worker's token, and
        //    the same run once it has ended.
        var unknownName = await FetchAsync(worker, token, runId, "NOT_A_PARAMETER");
        var unknownRun = await FetchAsync(worker, token, Guid.NewGuid(), FileName);

        var otherToken = await host.CreateWorkerTokenDirectlyAsync(
            await host.OrganizationIdAsync(),
            name: "file-other-agent");
        using var otherWorker = host.Host.CreateClient();
        var otherWorkerResult = await FetchAsync(otherWorker, otherToken, runId, FileName);

        using var completed = await RunTestHost.CompleteAsync(worker, token, runId, 0);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var endedRun = await FetchAsync(worker, token, runId, FileName);

        Assert.Equal(HttpStatusCode.NotFound, unknownName.Status);
        Assert.Equal(HttpStatusCode.NotFound, unknownRun.Status);
        Assert.Equal(HttpStatusCode.NotFound, otherWorkerResult.Status);
        Assert.Equal(HttpStatusCode.NotFound, endedRun.Status);

        Assert.Empty(unknownName.Bytes);
        Assert.Empty(unknownRun.Bytes);
        Assert.Empty(otherWorkerResult.Bytes);
        Assert.Empty(endedRun.Bytes);
    }

    [Fact]
    public async Task TC_002_a_step_reads_the_uploaded_file_at_a_path_in_the_run_directory()
    {
        var pipeline = $$"""
            pipeline {
              name = "file-parameter",
              steps = {
                { name = "Use", run = { [[{{PipelineTestData.DotNet}}]], [[{{PipelineTestData.ArgvEcho}}]],
                    "env", "{{FileName}}" } },
              },
            }
            """;

        using var repository = PipelineRepository.Create(pipeline);
        using var host = await RunTestHost.CreateAsync();
        using var run = await StepsRun.StartAsync(
            host,
            repository,
            prepareProject: (projectId, owner) => SaveFileParameterAsync(owner, projectId),
            file: ($"Parameters[{FileName}]", UploadName, Upload));

        var outcome = await run.ExecuteAsync();
        Assert.Equal(0, outcome.ExitCode);

        // The step read an absolute path inside this run's own directory, and the bytes are really there.
        var path = run.Lines
            .Select(line => line.Message)
            .FirstOrDefault(message => message.StartsWith(run.Work.TempPath, StringComparison.Ordinal)
                && message.Contains(FileName, StringComparison.Ordinal));

        Assert.NotNull(path);
        Assert.EndsWith(Path.Combine("parameters", FileName), path!, StringComparison.Ordinal);
        Assert.Equal(Upload, await File.ReadAllBytesAsync(path!, CancellationToken));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path!));
        }

        // The run's own cleanup owns the tree the file was written into.
        run.CleanupTemp();
        Assert.False(File.Exists(path!));
    }

    [Fact]
    public async Task A_file_parameter_the_worker_cannot_fetch_fails_the_run_before_any_step()
    {
        // The failure path, without a server: a claim that names a file parameter whose bytes the route does not
        // have. The run fails with its own reason - and that reason has to be one the worker may report, or the
        // server would refuse the completion and the run would sit Running forever.
        var runId = Guid.NewGuid();

        var root = Path.Combine(Path.GetTempPath(), $"zenkins-f041-files-{Guid.NewGuid():N}");
        try
        {
            var work = new WorkDirectoryService(root).Prepare(runId);
            var claim = new WorkerClaimResponse(
                RunId: runId,
                ProjectId: Guid.NewGuid(),
                RepositoryUrl: null,
                Branch: "main",
                PipelinePath: "pipeline.lua",
                LeaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
                FileParameters: [FileName]);

            var failure = await Assert.ThrowsAsync<RunParameterFileException>(() =>
                RunParameterFileMaterializer.MaterializeAsync(
                    claim,
                    work,
                    (_, _) => Task.FromResult<byte[]?>(null),
                    _ => { },
                    CancellationToken));

            Assert.Contains(FileName, failure.Message, StringComparison.Ordinal);
            Assert.True(
                PipelineFailureReasons.IsWorkerReportable(PipelineFailureReasons.ParameterFileUnavailable),
                "the run-level reason must be one the server accepts from a worker.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static async Task SaveFileParameterAsync(HttpClient owner, Guid projectId)
    {
        var drafts = JsonSerializer.Serialize(
            new[] { new ProjectParameterDraft(FileName, ProjectParameterTypes.File, null, null, "the licence file") },
            ProjectParameters.DraftsOptions);

        using var response = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}",
            $"/projects/{projectId}/parameters",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ParametersJson"] = drafts });

        Assert.True(
            (int)response.StatusCode is 302 or 303
                && response.Headers.Location!.OriginalString.Contains("notice=parameters-saved", StringComparison.Ordinal),
            $"saving the file parameter answered {(int)response.StatusCode}: {response.Headers.Location}.");
    }

    private static async Task<Guid> TriggerWithFileAsync(RunTestHost host, HttpClient owner, Guid projectId)
    {
        using var response = await OrganizationTestHost.PostMultipartFormAsync(
            owner,
            $"/projects/{projectId}",
            $"/projects/{projectId}/runs/start",
            // One ordinary field beside the token: a body whose fields all fail to bind arrives as a null form
            // (F-019), and the token is not a model field - a real browser always posts the branch input too.
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Branch"] = "main" },
            $"Parameters[{FileName}]",
            UploadName,
            Upload);

        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"the trigger answered {(int)response.StatusCode}: {response.Headers.Location}.");
        var location = response.Headers.Location!.OriginalString;
        Assert.True(location.StartsWith("/runs/", StringComparison.Ordinal), $"location was '{location}'");
        return Guid.Parse(location["/runs/".Length..]);
    }

    private static async Task<(HttpStatusCode Status, byte[] Bytes, string? CacheControl)> FetchAsync(
        HttpClient client,
        string workerToken,
        Guid runId,
        string name)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/workers/runs/{runId}/parameter-files")
        {
            Content = new StringContent(
                $$"""{"name":"{{name}}"}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", workerToken);

        using var response = await client.SendAsync(request, CancellationToken);
        var bytes = response.IsSuccessStatusCode
            ? await response.Content.ReadAsByteArrayAsync(CancellationToken)
            : [];
        return (response.StatusCode, bytes, response.Headers.CacheControl?.ToString());
    }
}
