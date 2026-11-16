namespace Zenkins.Server.Api;

/// <summary>
/// The API's knobs (card F-027), read from configuration once at startup (the <c>RunOptions</c> shape).
/// </summary>
/// <param name="RateLimitPerMinute">
/// The per-token request budget in one fixed minute. Sized so a script that triggers a deploy and polls a
/// run every second fits comfortably; the limiter exists to bound one leaked token, not to shape traffic.
/// </param>
/// <param name="MaxRateLimitBuckets">
/// How many tokens' counters the limiter may hold. A token id that no longer exists can still be flooded,
/// so the dictionary is bounded (the F-017 decision).
/// </param>
public sealed record ApiOptions(int RateLimitPerMinute, int MaxRateLimitBuckets)
{
    public const string RateLimitPerMinuteConfigurationKey = "Api:RateLimitPerMinute";
    public const string MaxRateLimitBucketsConfigurationKey = "Api:MaxRateLimitBuckets";

    public static readonly ApiOptions Defaults = new(RateLimitPerMinute: 120, MaxRateLimitBuckets: 1_024);

    public static ApiOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new ApiOptions(
            ReadPositive(configuration, RateLimitPerMinuteConfigurationKey, Defaults.RateLimitPerMinute),
            ReadPositive(configuration, MaxRateLimitBucketsConfigurationKey, Defaults.MaxRateLimitBuckets));
    }

    private static int ReadPositive(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], out var parsed) && parsed >= 1 ? parsed : fallback;
}
