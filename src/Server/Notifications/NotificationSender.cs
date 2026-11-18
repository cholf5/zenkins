using System.Net.Http.Headers;
using System.Text;
using Zenkins.Server.Data.Entities;
using Zenkins.Shared.Notifications;

namespace Zenkins.Server.Notifications;

/// <summary>One attempt's result: whether the receiver accepted it, and the words a page can show when it did not.</summary>
/// <param name="StatusCode">The HTTP status when one arrived - including a 3xx, which is a failure (see below).</param>
/// <param name="Error">The bounded, secret-free description of a failure; null when delivered.</param>
public sealed record NotificationSendResult(bool Delivered, int? StatusCode, string? Error)
{
    public static NotificationSendResult Ok(int statusCode) => new(true, statusCode, null);

    public static NotificationSendResult Failed(int? statusCode, string error) => new(false, statusCode, error);
}

/// <summary>
/// The one place a notification leaves the Server (card F-039): POST the body, sign it, bound the wait and the answer.
/// <para>
/// <b>Nothing here can delay a build.</b> The caller is the background dispatcher, never a terminal-state path, and
/// every attempt has its own timeout - a receiver that accepts a connection and then says nothing costs
/// <see cref="NotificationOptions.Timeout"/> and one recorded failure, not a hung request thread.
/// </para>
/// <para>
/// <b>A redirect is a failure, not a hop.</b> <c>AllowAutoRedirect = false</c> is part of the SSRF stance: an Owner
/// configures the URL, but a receiver that answers <c>302 Location: http://169.254.169.254/</c> must not turn this
/// feature into a way to reach the Server's own network. The 3xx is recorded and counts as a failed attempt.
/// </para>
/// <para>
/// <b>The response body is read up to a small bound</b> (<see cref="NotificationOptions.MaxResponseBytes"/>): enough
/// to quote "receiver said: ..." in the recorded error, never enough to matter for memory.
/// </para>
/// </summary>
public sealed class NotificationSender : IDisposable
{
    private readonly NotificationOptions _options;
    private readonly ILogger<NotificationSender> _logger;
    private readonly HttpClient _client;

    public NotificationSender(NotificationOptions options, ILogger<NotificationSender> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
        _client = new HttpClient(new SocketsHttpHandler
        {
            // Card F-039's SSRF stance: a configured URL is trusted, a redirect from it is not.
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = System.Net.DecompressionMethods.None,
        })
        {
            // Per-attempt, not per-client: the timeout is enforced with a linked token below so a shutdown can still
            // cancel immediately.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// Attempts one delivery. Never throws for a receiver-side problem: a timeout, a refused connection, a 4xx/5xx or
    /// a redirect all come back as <see cref="NotificationSendResult.Failed"/> with a bounded reason.
    /// </summary>
    public async Task<NotificationSendResult> SendAsync(
        string url,
        string? plaintextSecret,
        byte[] body,
        string deliveryId,
        string eventName,
        int attempt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(body);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            // The bytes that are signed are the bytes that are sent - one array, no re-serialization.
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        request.Headers.TryAddWithoutValidation(NotificationSignature.EventHeaderName, eventName);
        request.Headers.TryAddWithoutValidation(NotificationSignature.DeliveryHeaderName, deliveryId);
        request.Headers.TryAddWithoutValidation(
            NotificationSignature.AttemptHeaderName,
            attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (plaintextSecret is not null)
        {
            request.Headers.TryAddWithoutValidation(
                NotificationSignature.HeaderName,
                NotificationSignature.Compute(plaintextSecret, body));
        }

        try
        {
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadBoundedAsync(response, timeout.Token);
                return NotificationSendResult.Failed(
                    status,
                    string.IsNullOrWhiteSpace(detail) ? $"HTTP {status}" : $"HTTP {status}: {detail}");
            }

            return NotificationSendResult.Ok(status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return NotificationSendResult.Failed(
                null,
                $"no answer within {_options.Timeout.TotalSeconds:0.#}s");
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException or InvalidOperationException)
        {
            return NotificationSendResult.Failed(null, failure.Message);
        }
    }

    /// <summary>Reads at most <see cref="NotificationOptions.MaxResponseBytes"/> of the body, as one trimmed line.</summary>
    private async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[_options.MaxResponseBytes];
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                return string.Empty;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, read).Trim();
            var singleLine = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            return Truncate(singleLine, NotificationLimits.MaxErrorLength);
        }
        catch (Exception failure) when (failure is IOException or ObjectDisposedException or HttpRequestException)
        {
            // A body that breaks mid-read is not the reason the delivery failed; the status already is.
            _logger.LogDebug(failure, "A notification receiver's response body could not be read.");
            return string.Empty;
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    public void Dispose() => _client.Dispose();
}
