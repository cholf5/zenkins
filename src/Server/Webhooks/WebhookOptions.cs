using System.Globalization;

namespace Zenkins.Server.Webhooks;

/// <summary>
/// The webhook endpoint's knobs (card F-017), read from configuration once at startup - the same shape
/// as <see cref="Runs.RunOptions"/> and <see cref="Artifacts.ArtifactOptions"/>.
/// <para>
/// Every limit here bounds what an unauthenticated-but-signed endpoint can cost: the body cap decides
/// how much memory one delivery may occupy, the rate limit how many runs one (possibly leaked) secret
/// may queue, and the two windows how long a replayed delivery is remembered.
/// </para>
/// </summary>
/// <param name="MaxBodyBytes">
/// The largest delivery body accepted, counted <b>while reading</b> (never after the fact): 1 MB by
/// default, far above any real push payload and small enough that a flood cannot exhaust memory.
/// </param>
/// <param name="RateLimitPerMinute">
/// Deliveries accepted per webhook per minute. Shared by every delivery id on purpose: changing the
/// header must not buy a fresh budget.
/// </param>
/// <param name="DeliveryIdTtl">
/// How long a delivery id is remembered. Git hosts retry for hours, not days; a week is generous and
/// still bounded.
/// </param>
/// <param name="BodyHashWindow">
/// How long a body is de-duplicated when the sender sends no delivery id. Short, because two genuinely
/// separate pushes can carry an identical body (an empty commit replayed later must build again).
/// </param>
/// <param name="MaxRateLimitBuckets">
/// Upper bound on the limiter's buckets. The bucket key is the <em>raw</em> URL id (see
/// <see cref="WebhookRateLimiter"/>), so a flood of ids that never existed must not be able to grow the
/// map without limit.
/// </param>
/// <param name="SweepIntervalSeconds">How often the delivery-record sweeper runs.</param>
public sealed record WebhookOptions(
    int MaxBodyBytes,
    int RateLimitPerMinute,
    TimeSpan DeliveryIdTtl,
    TimeSpan BodyHashWindow,
    int MaxRateLimitBuckets,
    int SweepIntervalSeconds)
{
    public const string MaxBodyBytesConfigurationKey = "Webhooks:MaxBodyBytes";
    public const string RateLimitPerMinuteConfigurationKey = "Webhooks:RateLimitPerMinute";
    public const string DeliveryIdTtlDaysConfigurationKey = "Webhooks:DeliveryIdTtlDays";
    public const string BodyHashWindowMinutesConfigurationKey = "Webhooks:BodyHashWindowMinutes";
    public const string MaxRateLimitBucketsConfigurationKey = "Webhooks:MaxRateLimitBuckets";
    public const string SweepIntervalSecondsConfigurationKey = "Webhooks:SweepIntervalSeconds";

    /// <summary>The 1 MB cap; a push payload is measured in kilobytes.</summary>
    public const int DefaultMaxBodyBytes = 1024 * 1024;

    public const int DefaultRateLimitPerMinute = 60;

    public const int DefaultMaxRateLimitBuckets = 100_000;

    public static WebhookOptions Defaults { get; } = new(
        MaxBodyBytes: DefaultMaxBodyBytes,
        RateLimitPerMinute: DefaultRateLimitPerMinute,
        DeliveryIdTtl: TimeSpan.FromDays(7),
        BodyHashWindow: TimeSpan.FromMinutes(10),
        MaxRateLimitBuckets: DefaultMaxRateLimitBuckets,
        SweepIntervalSeconds: 3600);

    public static WebhookOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new WebhookOptions(
            ReadBytes(configuration, MaxBodyBytesConfigurationKey, Defaults.MaxBodyBytes),
            ReadCount(configuration, RateLimitPerMinuteConfigurationKey, Defaults.RateLimitPerMinute),
            ReadWindow(configuration, DeliveryIdTtlDaysConfigurationKey, Defaults.DeliveryIdTtl, perDay: true),
            ReadWindow(
                configuration,
                BodyHashWindowMinutesConfigurationKey,
                Defaults.BodyHashWindow,
                perDay: false),
            ReadCount(
                configuration,
                MaxRateLimitBucketsConfigurationKey,
                Defaults.MaxRateLimitBuckets),
            ReadCount(configuration, SweepIntervalSecondsConfigurationKey, Defaults.SweepIntervalSeconds));
    }

    private static int ReadBytes(IConfiguration configuration, string key, int fallback)
    {
        var text = configuration[key];
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 1
                ? parsed
                : fallback;
    }

    private static int ReadCount(IConfiguration configuration, string key, int fallback)
    {
        var text = configuration[key];
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 1
                ? parsed
                : fallback;
    }

    private static TimeSpan ReadWindow(IConfiguration configuration, string key, TimeSpan fallback, bool perDay)
    {
        var text = configuration[key];
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            return fallback;
        }

        return perDay ? TimeSpan.FromDays(parsed) : TimeSpan.FromMinutes(parsed);
    }
}
