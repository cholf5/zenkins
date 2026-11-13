using Zenkins.Shared.Artifacts;

namespace Zenkins.Worker.Artifacts;

/// <summary>
/// The run-scoped artifact protocol client (card F-016): <c>create → files → finalize</c>, with the worker
/// token already bound. It exists so <see cref="ArtifactPublisher"/> depends on the three verbs it needs
/// rather than on the whole worker API surface - and so a test can drive the publisher against a fake
/// without an HTTP server.
/// <para>
/// Every call is run-scoped and carries the run's cancellation token: a cancel or the run's deadline kills
/// an upload mid-flight, which is exactly what <c>VerifyArtifactUpload</c> is expected to observe. A refusal
/// surfaces as <see cref="ArtifactUploadRejectedException"/> with the server's own message.
/// </para>
/// </summary>
public sealed class ArtifactUploadClient(WorkerApiClient api, string workerToken) : IArtifactProtocolClient
{
    /// <summary>Reserves the artifact row; returns its id.</summary>
    public async Task<Guid> CreateAsync(
        Guid runId,
        string name,
        string kind,
        CancellationToken cancellationToken)
    {
        var response = await api.CreateArtifactAsync(
            workerToken,
            runId,
            new ArtifactCreateRequest(name, kind),
            cancellationToken);
        return response.ArtifactId;
    }

    /// <summary>Streams one file (raw body, path and SHA-256 as query parameters) and returns the stored entry.</summary>
    public Task<ArtifactFileEntry> UploadFileAsync(
        Guid runId,
        Guid artifactId,
        string relativePath,
        string sha256,
        string filePath,
        CancellationToken cancellationToken) =>
        api.UploadArtifactFileAsync(
            workerToken,
            runId,
            artifactId,
            relativePath,
            sha256,
            filePath,
            cancellationToken);

    /// <summary>Publishes the artifact (the server verifies the set, writes the manifest, flips to Ready).</summary>
    public Task<ArtifactFinalizeResponse> FinalizeAsync(
        Guid runId,
        Guid artifactId,
        ArtifactFileEntry[] files,
        CancellationToken cancellationToken) =>
        api.FinalizeArtifactAsync(workerToken, runId, artifactId, files, cancellationToken);
}
