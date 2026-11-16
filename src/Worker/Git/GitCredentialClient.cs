using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Zenkins.Worker.Git;

/// <summary>
/// The worker's credential fetch (card F-013): one run-scoped request, answered with the project's
/// credential material for the worker currently assigned to that run.
/// <para>
/// <b>Per-run, worker-authenticated, never in the claim payload.</b> The claim response is queued,
/// retried and logged in more places than a secret should be, so the material is fetched at clone time
/// instead - with the worker's own token, for the run id the worker is holding, and nowhere else.
/// </para>
/// <para>
/// <b>404 means "no material", not "an error".</b> The server collapses an unknown run, another
/// tenant's run, another worker's run and a project without a credential into the same bodyless 404, so
/// this client treats it as <see langword="null"/>: the run clones anonymously (or fails on the
/// remote's own terms) instead of retrying something a retry cannot fix. Every other non-success status
/// is a definitive rejection and surfaces as a <see cref="GitException"/> whose message carries the
/// status code only - never a response body, which could contain the material.
/// </para>
/// </summary>
public sealed class GitCredentialClient
{
    /// <summary>The worker API route (mirrors <c>GitCredentialEndpoints.WorkerFetchPath</c>).</summary>
    public const string RouteFormat = "api/workers/runs/{0}/git-credential";

    /// <summary>
    /// Card I-005's probe-scoped route (mirrors <c>GitCredentialEndpoints.ProbeFetchPath</c>): the same
    /// material, addressed by a probe id. The two routes are separate on purpose - the Server resolves a
    /// different lease behind each - so neither id can be replayed on the other.
    /// </summary>
    public const string ProbeRouteFormat = "api/workers/probes/{0}/git-credential";

    /// <summary>The per-request ceiling: a hung server must not hold the clone forever.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;

    public GitCredentialClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    /// <summary>
    /// Fetches the credential material for <paramref name="runId"/>, or <see langword="null"/> when the
    /// server has none for this worker (the 404 answer).
    /// </summary>
    /// <exception cref="GitException">The server answered something other than 200/404.</exception>
    public Task<GitCredentialMaterial?> FetchAsync(
        string workerToken,
        Guid runId,
        CancellationToken cancellationToken = default) =>
        FetchAsync(workerToken, RouteFormat, runId, "run", cancellationToken);

    /// <summary>
    /// Fetches the material for one probe this worker holds (card I-005), or <see langword="null"/> when
    /// the server has none (the 404 answer). Same shapes, same timeouts and the same secrecy rules as the
    /// run fetch - only the route differs, because the Server authorizes a different lease behind it.
    /// </summary>
    public Task<GitCredentialMaterial?> FetchForProbeAsync(
        string workerToken,
        Guid probeId,
        CancellationToken cancellationToken = default) =>
        FetchAsync(workerToken, ProbeRouteFormat, probeId, "probe", cancellationToken);

    private async Task<GitCredentialMaterial?> FetchAsync(
        string workerToken,
        string routeFormat,
        Guid id,
        string subject,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            string.Format(System.Globalization.CultureInfo.InvariantCulture, routeFormat, id.ToString("D")));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", workerToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitException(
                $"the server did not answer the Git credential request for {subject} {id} within "
                + $"{RequestTimeout.TotalSeconds:0}s.",
                GitFailureKind.Failed);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new GitException(
                    $"the server rejected the worker's token while fetching the {subject}'s Git credential.",
                    GitFailureKind.AuthenticationRejected);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new GitException(
                    $"the server answered {(int)response.StatusCode} while fetching the Git credential for {subject} {id}.",
                    GitFailureKind.Failed);
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return Parse(body, id);
        }
    }

    /// <summary>
    /// Parses the material body (<c>{ kind, value, userName }</c>). The value is the secret, so a
    /// malformed body produces a message about the shape only - never a dump of what arrived.
    /// </summary>
    private static GitCredentialMaterial Parse(string body, Guid subjectId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new GitException(
                $"the Git credential response for {subjectId} was not valid JSON.",
                GitFailureKind.Failed);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("kind", out var kindElement)
                || !root.TryGetProperty("value", out var valueElement)
                || kindElement.ValueKind != JsonValueKind.String
                || valueElement.ValueKind != JsonValueKind.String)
            {
                throw new GitException(
                    $"the Git credential response for {subjectId} did not have the expected kind/value shape.",
                    GitFailureKind.Failed);
            }

            GitCredentialKind kind;
            try
            {
                kind = GitCredentialMaterial.ParseKind(kindElement.GetString());
            }
            catch (ArgumentException)
            {
                throw new GitException(
                    $"the Git credential response for {subjectId} named an unsupported kind.",
                    GitFailureKind.Credential);
            }

            var userName = root.TryGetProperty("userName", out var userNameElement)
                && userNameElement.ValueKind == JsonValueKind.String
                    ? userNameElement.GetString()
                    : null;

            return new GitCredentialMaterial(kind, valueElement.GetString()!, userName);
        }
    }
}
