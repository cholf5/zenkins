using System.IO.Compression;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Zenkins.Server.Workers;
using Zenkins.Shared.Artifacts;

namespace Zenkins.Server.Artifacts;

/// <summary>
/// HTTP endpoints for card F-016: the worker-artifact API (<c>create → files → finalize</c>, under
/// <c>/api/workers/*</c>, token-authenticated) and the per-artifact download under <c>/runs/*</c>
/// (cookie-authenticated, tenant-scoped).
/// <para>
/// <b>No multipart, no <c>IFormFile</c>.</b> A file's bytes are the raw request body, streamed straight
/// into the store. Multipart would buffer the part to a spool file and drag in the framework's form limits;
/// the point of the protocol is that the Server never holds an artifact in memory, and that a future
/// object-store backend can implement the same three verbs.
/// </para>
/// <para>
/// <b>Every refusal is explicit.</b> The caps answer 413, a path or payload the rules refuse answers 400
/// with a code and a human-readable message (which the Worker copies into the step's log line), a state
/// conflict answers 409, and a foreign or unknown id answers a bare 404.
/// </para>
/// </summary>
public static class ArtifactEndpoints
{
    /// <summary>Content type of a single-file artifact download.</summary>
    public const string FileContentType = "application/octet-stream";

    /// <summary>Content type of a directory artifact's on-the-fly zip.</summary>
    public const string ZipContentType = "application/zip";

    public static IEndpointRouteBuilder MapArtifactEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Worker API: deliberately NOT RequireAuthorization (the token authenticates and selects the
        // tenant - a session cookie must never authorize a worker action).
        var api = endpoints.MapGroup("/api/workers");
        api.MapPost("/runs/{runId:guid}/artifacts", CreateAsync);
        api.MapPost("/runs/{runId:guid}/artifacts/{artifactId:guid}/files", UploadFileAsync);
        api.MapPost("/runs/{runId:guid}/artifacts/{artifactId:guid}/finalize", FinalizeAsync);

        // Download: cookie-authenticated like the pages. The tenant comes from the session, never the URL,
        // so a foreign organization's artifact is the same 404 as an unknown id.
        endpoints.MapGet("/runs/{runId:guid}/artifacts/{artifactId:guid}/download", DownloadAsync)
            .RequireAuthorization();

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        Guid runId,
        [FromBody] ArtifactCreateRequest request,
        HttpContext httpContext,
        ArtifactService artifacts)
    {
        var result = await artifacts.CreateAsync(
            WorkerEndpoints.BearerToken(httpContext),
            runId,
            request,
            httpContext.RequestAborted);

        return result.Status switch
        {
            ArtifactOperationStatus.Success => Results.Json(
                new ArtifactCreateResponse(result.ArtifactId!.Value, ArtifactStatus.Pending)),
            ArtifactOperationStatus.InvalidToken => Results.Unauthorized(),
            ArtifactOperationStatus.NotFound => Results.NotFound(),
            _ => Error(result.Status, result.ErrorCode, result.Message),
        };
    }

    private static async Task<IResult> UploadFileAsync(
        Guid runId,
        Guid artifactId,
        [FromQuery] string? path,
        [FromQuery] string? sha256,
        HttpContext httpContext,
        ArtifactService artifacts)
    {
        // Kestrel's default request-body ceiling (~30 MB) is far below the per-file cap, so it is raised
        // here to exactly that cap: a body over it is refused by the transport before a byte is written,
        // and a body under it is streamed. The feature is only writable before the body is read.
        var bodyLimit = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false })
        {
            bodyLimit.MaxRequestBodySize = artifacts.Limits.MaxFileBytes;
        }

        var result = await artifacts.UploadFileAsync(
            WorkerEndpoints.BearerToken(httpContext),
            runId,
            artifactId,
            path,
            sha256,
            httpContext.Request.ContentLength,
            httpContext.Request.Body,
            httpContext.RequestAborted);

        return result.Status switch
        {
            ArtifactOperationStatus.Success => Results.Json(result.File),
            ArtifactOperationStatus.InvalidToken => Results.Unauthorized(),
            ArtifactOperationStatus.NotFound => Results.NotFound(),
            _ => Error(result.Status, result.ErrorCode, result.Message),
        };
    }

    private static async Task<IResult> FinalizeAsync(
        Guid runId,
        Guid artifactId,
        [FromBody] ArtifactFinalizeRequest request,
        HttpContext httpContext,
        ArtifactService artifacts)
    {
        var result = await artifacts.FinalizeAsync(
            WorkerEndpoints.BearerToken(httpContext),
            runId,
            artifactId,
            request,
            httpContext.RequestAborted);

        return result.Status switch
        {
            ArtifactOperationStatus.Success => Results.Json(result.Artifact),
            ArtifactOperationStatus.InvalidToken => Results.Unauthorized(),
            ArtifactOperationStatus.NotFound => Results.NotFound(),
            _ => Error(result.Status, result.ErrorCode, result.Message),
        };
    }

    private static async Task<IResult> DownloadAsync(
        Guid runId,
        Guid artifactId,
        HttpContext httpContext,
        ArtifactService artifacts)
    {
        var download = await artifacts.OpenDownloadAsync(runId, artifactId, httpContext.RequestAborted);
        if (download is null)
        {
            // Foreign organization, unknown id, not-yet-finalized or incomplete: one plain 404, no body,
            // no existence oracle. Nothing is streamed, so there is no timing difference to read either.
            return Results.NotFound();
        }

        var artifact = download.Artifact;
        if (artifact.Kind == ArtifactKind.File)
        {
            var relativePath = download.RelativeFiles[0];
            var fullPath = artifacts.ResolveStoredFile(artifact, relativePath);
            var fileName = ArtifactDownloadName.Sanitize(
                Path.GetFileName(relativePath.Replace(ArtifactPathRules.Separator, Path.DirectorySeparatorChar)),
                $"artifact-{artifact.Id:D}");
            return Results.Stream(
                output => artifacts.CopyStoredFileAsync(fullPath, output, httpContext.RequestAborted),
                FileContentType,
                fileName);
        }

        // A directory artifact is zipped on the fly: entries come straight from the store into the response
        // body, so no temporary archive ever exists on disk and memory stays bounded by the copy buffer.
        //
        // ZipArchive writes its headers synchronously, and ASP.NET Core disallows synchronous response IO by
        // default. The opt-in is scoped to this request (and this endpoint only) - the alternative would be
        // buffering the whole archive, which is exactly the memory bound this card forbids.
        if (httpContext.Features.Get<IHttpBodyControlFeature>() is { } bodyControl)
        {
            bodyControl.AllowSynchronousIO = true;
        }

        var zipName = ArtifactDownloadName.Sanitize(
            artifact.Name + ".zip",
            $"artifact-{artifact.Id:D}.zip");
        return Results.Stream(
            async output =>
            {
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var relativePath in download.RelativeFiles)
                {
                    // The entry name is recomputed through the shared rules and must already be canonical:
                    // an absolute path or a '..' entry is never written into an archive we hand out.
                    if (!ArtifactPathRules.TryNormalize(relativePath, out var entryName, out _)
                        || !string.Equals(entryName, relativePath, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                    await using var entryStream = entry.Open();
                    await artifacts.CopyStoredFileAsync(
                        artifacts.ResolveStoredFile(artifact, relativePath),
                        entryStream,
                        httpContext.RequestAborted);
                }
            },
            ZipContentType,
            zipName);
    }

    private static IResult Error(ArtifactOperationStatus status, string? code, string? message) =>
        Results.Json(
            new ArtifactErrorResponse(code ?? ArtifactErrorCodes.FieldInvalid, message ?? "the artifact request was refused."),
            statusCode: status switch
            {
                ArtifactOperationStatus.TooLarge => StatusCodes.Status413PayloadTooLarge,
                ArtifactOperationStatus.Conflict => StatusCodes.Status409Conflict,
                ArtifactOperationStatus.NotFound => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status400BadRequest,
            });

    /// <summary>The refusal body: a stable code plus the message the Worker puts in its log line.</summary>
    public sealed record ArtifactErrorResponse(string Error, string Message);
}

/// <summary>
/// The <c>Content-Disposition</c> name sanitiser (card F-016): a header value built from a pipeline-declared
/// path must not be able to inject a second header (CR/LF), confuse a proxy with a quoted-string, or smuggle
/// a path separator into the file name a browser saves. Everything outside a small safe set becomes
/// <c>_</c>, leading/trailing dots and spaces are trimmed (so no <c>..</c> name and no hidden file), and an
/// empty result falls back to a generated name.
/// </summary>
public static class ArtifactDownloadName
{
    /// <summary>The longest name a download is willing to suggest (a file system's own limit is usually 255).</summary>
    public const int MaxLength = 120;

    /// <summary>A header-safe download name derived from <paramref name="declared"/>, or <paramref name="fallback"/>.</summary>
    public static string Sanitize(string? declared, string fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);

        if (string.IsNullOrWhiteSpace(declared))
        {
            return fallback;
        }

        var builder = new System.Text.StringBuilder(declared.Length);
        foreach (var character in declared)
        {
            // ASCII only: RFC 6266 allows an UTF-8 filename* parameter, but a name that is not ASCII is not
            // worth the compatibility surface - and a browser that ignores filename* would save mojibake.
            var safe = character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                or '.' or '-' or '_' or ' ';
            builder.Append(safe ? character : '_');
        }

        var cleaned = builder.ToString().Trim(' ', '.');
        if (cleaned.Length > MaxLength)
        {
            var extension = Path.GetExtension(cleaned);
            var keep = Math.Max(1, MaxLength - extension.Length);
            cleaned = cleaned[..keep] + extension;
        }

        return cleaned.Length == 0 ? fallback : cleaned;
    }
}
