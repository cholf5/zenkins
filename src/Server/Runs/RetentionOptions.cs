using Microsoft.Extensions.Configuration;

namespace Zenkins.Server.Runs;

/// <summary>
/// The retention sweeper's own knob (card F-038), read from configuration once at startup like
/// <see cref="RunOptions"/>. The card asks for a daily sweep; the interval is configurable so a test can drive
/// the real loop instead of waiting a day, and so an operator can shorten it on a small installation.
/// </summary>
/// <param name="SweepIntervalHours">
/// How often the full (all projects) sweep runs, in hours. Evaluations after a terminal run do not wait for this:
/// they are triggered by the run's own completion and only look at that run's project.
/// </param>
public sealed record RetentionOptions(TimeSpan SweepInterval)
{
    public const string SweepIntervalHoursConfigurationKey = "Retention:SweepIntervalHours";

    /// <summary>One day, the card's "daily sweep".</summary>
    public static readonly RetentionOptions Defaults = new(TimeSpan.FromHours(24));

    public static RetentionOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var text = configuration[SweepIntervalHoursConfigurationKey];
        return double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var hours)
            && hours > 0
                ? new RetentionOptions(TimeSpan.FromHours(hours))
                : Defaults;
    }
}
