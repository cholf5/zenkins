using System.Globalization;
using Zenkins.Shared.Artifacts;

namespace Zenkins.Server.Artifacts;

/// <summary>
/// The artifact store's knobs (card F-016): where the bytes live, the three upload caps and the sweeper's
/// schedule. Read from configuration once at startup, the same shape as <see cref="Runs.RunOptions"/> and
/// <see cref="Zenkins.Server.Data.ZenkinsDataOptions"/>.
/// <para>
/// <b>The store root is data, like the database.</b> <c>Artifacts:RootPath</c> defaults to
/// <c>App_Data/artifacts</c> under the content root - next to <c>App_Data/zenkins.db</c>, so a backup of
/// the data directory is a backup of the build history. A relative value is resolved against the content
/// root at first use (not at registration), so a deployment's configuration source cannot land after the
/// path was frozen.
/// </para>
/// </summary>
/// <param name="RootPath">Absolute path of the artifact store's root directory.</param>
/// <param name="Limits">The three caps every upload is checked against.</param>
/// <param name="SweepIntervalSeconds">How often the abandoned-upload sweeper runs.</param>
/// <param name="PendingTimeout">
/// How long a <c>Pending</c> artifact may stay <c>Pending</c> before the sweeper calls it abandoned. A
/// real upload streams within seconds; the window only has to be longer than the slowest legitimate one.
/// </param>
public sealed record ArtifactOptions(
    string RootPath,
    ArtifactLimits Limits,
    int SweepIntervalSeconds,
    TimeSpan PendingTimeout)
{
    /// <summary>Configuration key of the store root.</summary>
    public const string RootPathConfigurationKey = "Artifacts:RootPath";

    /// <summary>Configuration key of the sweeper's interval.</summary>
    public const string SweepIntervalSecondsConfigurationKey = "Artifacts:SweepIntervalSeconds";

    /// <summary>Configuration key of the abandoned-upload window.</summary>
    public const string PendingTimeoutMinutesConfigurationKey = "Artifacts:PendingTimeoutMinutes";

    /// <summary>The default store root, relative to the content root.</summary>
    public const string DefaultRootPath = "App_Data/artifacts";

    /// <summary>The defaults used when nothing is configured (tests override the small parts).</summary>
    public static ArtifactOptions Defaults { get; } = new(
        Path.Combine("App_Data", "artifacts"),
        ArtifactLimits.Defaults,
        SweepIntervalSeconds: 60,
        PendingTimeout: TimeSpan.FromMinutes(30));

    /// <summary>Reads the options, resolving a relative store root against <paramref name="contentRootPath"/>.</summary>
    public static ArtifactOptions FromConfiguration(IConfiguration configuration, string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        return new ArtifactOptions(
            ResolveRootPath(configuration, contentRootPath),
            ArtifactLimits.FromConfiguration(key => configuration[key]),
            ReadSeconds(configuration, SweepIntervalSecondsConfigurationKey, Defaults.SweepIntervalSeconds),
            ReadTimeout(configuration));
    }

    /// <summary>
    /// The absolute store root. Exposed separately because the startup composition needs it before the
    /// options object exists (the directory is created and asserted writable like the key ring).
    /// </summary>
    public static string ResolveRootPath(IConfiguration configuration, string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var configured = configuration[RootPathConfigurationKey];
        var path = string.IsNullOrWhiteSpace(configured) ? DefaultRootPath : configured;
        return Path.GetFullPath(Path.Combine(Path.GetFullPath(contentRootPath), path));
    }

    private static int ReadSeconds(IConfiguration configuration, string key, int fallback)
    {
        var text = configuration[key];
        return int.TryParse(text, out var parsed) && parsed >= 1 ? parsed : fallback;
    }

    private static TimeSpan ReadTimeout(IConfiguration configuration)
    {
        var text = configuration[PendingTimeoutMinutesConfigurationKey];
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
            && minutes > 0
                ? TimeSpan.FromMinutes(minutes)
                : Defaults.PendingTimeout;
    }
}
