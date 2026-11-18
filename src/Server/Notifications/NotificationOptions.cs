using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Zenkins.Server.Notifications;

/// <summary>
/// The outbound notification knobs (card F-039), read from configuration once at startup like
/// <c>RunOptions</c>. Tests configure a zero backoff and a tiny attempt bound instead of waiting on real time.
/// </summary>
/// <param name="TimeoutSeconds">
/// One HTTP request's budget. A receiver that accepts the connection and then says nothing must not hold a dispatcher
/// pass; the attempt fails with "timed out" and is retried (or settles) like any other failure.
/// </param>
/// <param name="MaxAttempts">
/// How many attempts one obligation gets before it settles as <see cref="NotificationOutcomes.Failed"/>. "Never stuck,
/// never infinite" is the card's requirement: a dead endpoint ends in a recorded failure.
/// </param>
/// <param name="BackoffSeconds">
/// The base of the linear backoff: attempt N waits N x this. A receiver that is down for a minute recovers without
/// the operator touching anything, and a receiver that is gone stops costing requests after
/// <paramref name="MaxAttempts"/>.
/// </param>
/// <param name="MaxResponseBytes">
/// How much of the receiver's answer is read. The body is only ever used for the recorded error, so a receiver that
/// answers with a gigabyte must not become a memory story.
/// </param>
/// <param name="PublicBaseUrl">
/// The Server's own public base URL, used to build the payload's <c>run.url</c>. Unset means the link is the
/// relative path (<c>/runs/{id}</c>) - honest about not knowing the host, rather than inventing one.
/// </param>
/// <param name="PollIntervalSeconds">How often the dispatcher looks for due obligations.</param>
public sealed record NotificationOptions(
    TimeSpan Timeout,
    int MaxAttempts,
    TimeSpan Backoff,
    int MaxResponseBytes,
    string? PublicBaseUrl,
    TimeSpan PollInterval)
{
    public const string TimeoutSecondsKey = "Notifications:TimeoutSeconds";
    public const string MaxAttemptsKey = "Notifications:MaxAttempts";
    public const string BackoffSecondsKey = "Notifications:BackoffSeconds";
    public const string MaxResponseBytesKey = "Notifications:MaxResponseBytes";
    public const string PublicBaseUrlKey = "Notifications:PublicBaseUrl";
    public const string PollIntervalSecondsKey = "Notifications:PollIntervalSeconds";

    public static readonly NotificationOptions Defaults = new(
        Timeout: TimeSpan.FromSeconds(10),
        MaxAttempts: 5,
        Backoff: TimeSpan.FromSeconds(5),
        MaxResponseBytes: 4096,
        PublicBaseUrl: null,
        PollInterval: TimeSpan.FromSeconds(2));

    public static NotificationOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new NotificationOptions(
            Timeout: TimeSpan.FromSeconds(ReadNumber(configuration, TimeoutSecondsKey, Defaults.Timeout.TotalSeconds)),
            MaxAttempts: (int)ReadNumber(configuration, MaxAttemptsKey, Defaults.MaxAttempts),
            Backoff: TimeSpan.FromSeconds(ReadNumber(
                configuration, BackoffSecondsKey, Defaults.Backoff.TotalSeconds, allowZero: true)),
            MaxResponseBytes: (int)ReadNumber(
                configuration, MaxResponseBytesKey, Defaults.MaxResponseBytes),
            PublicBaseUrl: NormalizeBaseUrl(configuration[PublicBaseUrlKey]),
            PollInterval: TimeSpan.FromSeconds(
                ReadNumber(configuration, PollIntervalSecondsKey, Defaults.PollInterval.TotalSeconds)));
    }

    /// <summary>
    /// A number, or the default when the key is unset or unreadable. Zero is legal only where the caller allows it
    /// (<c>Notifications:BackoffSeconds=0</c> means "retry immediately", which a test and a small deployment both want);
    /// a timeout or an attempt bound of zero would mean "never", so those keep falling back.
    /// </summary>
    private static double ReadNumber(
        IConfiguration configuration,
        string key,
        double fallback,
        bool allowZero = false) =>
        double.TryParse(configuration[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && (allowZero ? parsed >= 0 : parsed > 0)
                ? parsed
                : fallback;

    /// <summary>
    /// The base URL without its trailing slash, or null. A value that is not an absolute http(s) URL is ignored
    /// rather than stored: an unusable link is worse than the honest relative path.
    /// </summary>
    private static string? NormalizeBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                ? uri.ToString().TrimEnd('/')
                : null;
    }
}
