using System.Globalization;

namespace Zenkins.Worker;

/// <summary>
/// The parsed and validated worker configuration, read from environment variables
/// (card F-005; F-007 adds the run-execution knobs). Validation happens once at startup: invalid
/// configuration produces a clear one-line error and a non-zero exit - never a stack trace.
/// <para>
/// <c>ZENKINS_DATA_DIR</c> is an extra knob beyond the card's table: it overrides the directory the
/// worker token is persisted under (default: <c>.zenkins</c> inside the user profile). It exists so
/// the process-level E2E can run without touching the real user profile, and so operators can place
/// the credential on a mounted secret volume.
/// </para>
/// <para>
/// F-007 adds: <c>ZENKINS_WORK_ROOT</c> (the per-run work directory root, default <c>work/</c>
/// resolved — like any relative value — from <see cref="AppContext.BaseDirectory"/> via
/// <see cref="ResolveWorkRoot"/>, never the current directory; card I-003),
/// <c>ZENKINS_CLEANUP_TEMP</c> (delete the run's <c>temp/</c> after the run, default <c>true</c>),
/// <c>ZENKINS_WORKER_CONCURRENCY</c> (runs executed simultaneously; registered and heartbeated to the
/// server, which enforces it as <c>MaxConcurrency</c>, default 1) and <c>ZENKINS_CLAIM_SECONDS</c> (the
/// poll interval between empty claims, default 2).
/// </para>
/// <para>
/// F-008 adds: <c>ZENKINS_LOG_BUFFER_LINES</c> (the per-run bound of <em>unsent</em> log lines
/// held while the upstream connection is down; drop-oldest beyond it, with an honest synthetic
/// marker line reporting the loss; default 10 000).
/// </para>
/// <para>
/// F-013 adds the Git knobs: <c>ZENKINS_GIT_DEPTH</c> (clone depth; <c>0</c>, the default, is a full
/// clone), <c>ZENKINS_GIT_KNOWN_HOSTS</c> (a pinned known-hosts file for SSH remotes; when unset the
/// worker uses <c>accept-new</c> against a per-run file) and <c>ZENKINS_GIT_TIMEOUT_SECONDS</c> (the
/// budget of one git invocation, always additionally capped by what is left of the run's deadline).
/// </para>
/// </summary>
public sealed record WorkerConfiguration(
    Uri ServerUrl,
    string? RegistrationToken,
    string WorkerName,
    string Tags,
    TimeSpan HeartbeatInterval,
    string DataDirectory,
    string WorkRoot,
    bool CleanupTemp,
    int MaxConcurrency,
    TimeSpan ClaimPollInterval,
    int LogBufferLines,
    int GitDepth = WorkerConfiguration.DefaultGitDepth,
    string? GitKnownHostsPath = null,
    TimeSpan? GitOperationTimeout = null)
{
    public const string ServerUrlVariable = "ZENKINS_SERVER_URL";
    public const string RegistrationTokenVariable = "ZENKINS_REGISTRATION_TOKEN";
    public const string WorkerNameVariable = "ZENKINS_WORKER_NAME";
    public const string WorkerTagsVariable = "ZENKINS_WORKER_TAGS";
    public const string HeartbeatSecondsVariable = "ZENKINS_HEARTBEAT_SECONDS";
    public const string DataDirectoryVariable = "ZENKINS_DATA_DIR";
    public const string WorkRootVariable = "ZENKINS_WORK_ROOT";
    public const string CleanupTempVariable = "ZENKINS_CLEANUP_TEMP";
    public const string WorkerConcurrencyVariable = "ZENKINS_WORKER_CONCURRENCY";
    public const string ClaimSecondsVariable = "ZENKINS_CLAIM_SECONDS";
    public const string LogBufferLinesVariable = "ZENKINS_LOG_BUFFER_LINES";
    public const string GitDepthVariable = "ZENKINS_GIT_DEPTH";
    public const string GitKnownHostsVariable = "ZENKINS_GIT_KNOWN_HOSTS";
    public const string GitTimeoutSecondsVariable = "ZENKINS_GIT_TIMEOUT_SECONDS";

    /// <summary>PRD §3.4.5: default heartbeat interval and maximum concurrent tasks of 1.</summary>
    public const int DefaultHeartbeatSeconds = 15;

    /// <summary>
    /// The absolute work root the run directories live under (card I-003). A relative value — the
    /// default <c>work</c> or a configured relative <c>ZENKINS_WORK_ROOT</c> — resolves against
    /// <see cref="AppContext.BaseDirectory"/>, never the process working directory: a service-packaged
    /// worker (launchd plist, Windows service) has no meaningful current directory, and the single-file
    /// drop must keep its runs beside its own binary. <c>Assembly.Location</c> is banned for the same
    /// reason it is on the server (IL3000 under single-file). An absolute value passes through
    /// unchanged.
    /// </summary>
    public string ResolveWorkRoot() => Path.GetFullPath(WorkRoot, AppContext.BaseDirectory);

    /// <summary>F-007 defaults: work root, temp cleanup on, concurrency 1, 2s claim polling.</summary>
    public const string DefaultWorkRoot = "work";
    public const bool DefaultCleanupTemp = true;
    public const int DefaultMaxConcurrency = 1;
    public const double DefaultClaimSeconds = 2;

    /// <summary>F-008 default: keep up to 10 000 unsent lines per run while reconnecting.</summary>
    public const int DefaultLogBufferLines = 10_000;

    /// <summary>Sane bounds: a worker cannot run zero runs, and neither the worker nor the server
    /// benefits from a four-digit concurrency.</summary>
    public const int MinMaxConcurrency = 1;
    public const int MaxMaxConcurrency = 1024;

    /// <summary>Claim polling below a tenth of a second is a hot loop against the server.</summary>
    public const double MinClaimSeconds = 0.1;
    public const double MaxClaimSeconds = 3600;

    /// <summary>Sane bounds for the per-run unsent-line buffer: a one-line buffer would drop almost
    /// everything on the first hiccup; a million lines is already an outlier-sized run.</summary>
    public const int MinLogBufferLines = 1;
    public const int MaxLogBufferLines = 1_000_000;

    /// <summary>
    /// F-013 defaults: full clone (depth 0), no pinned known-hosts file, five minutes per git
    /// invocation. A depth is opt-in because a shallow clone cannot resolve an older pinned commit.
    /// </summary>
    public const int DefaultGitDepth = 0;

    /// <summary>One git invocation's default budget: a clone of a large repository, not a hung one.</summary>
    public static readonly TimeSpan DefaultGitOperationTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Bounds for <c>ZENKINS_GIT_DEPTH</c>: 0 (full) up to a six-digit depth.</summary>
    public const int MinGitDepth = 0;
    public const int MaxGitDepth = 100_000;

    /// <summary>Bounds for <c>ZENKINS_GIT_TIMEOUT_SECONDS</c>: one second up to a day.</summary>
    public const int MinGitTimeoutSeconds = 1;
    public const int MaxGitTimeoutSeconds = 86_400;

    private static readonly string DefaultDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".zenkins");

    /// <summary>
    /// Parses the environment. Returns the configuration, or a one-line error message safe to print
    /// to stderr before exiting non-zero.
    /// </summary>
    public static WorkerConfigurationResult FromEnvironment(IReadOnlyDictionary<string, string?> environment)
    {
        var serverUrlText = environment.GetValueOrDefault(ServerUrlVariable)?.Trim();
        if (string.IsNullOrEmpty(serverUrlText))
        {
            return WorkerConfigurationResult.Invalid(
                $"configuration error: {ServerUrlVariable} is required (the Zenkins Server base URL, e.g. http://localhost:5067).");
        }

        if (!Uri.TryCreate(serverUrlText, UriKind.Absolute, out var serverUrl)
            || (serverUrl.Scheme != Uri.UriSchemeHttp && serverUrl.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(serverUrl.Host))
        {
            return WorkerConfigurationResult.Invalid(
                $"configuration error: {ServerUrlVariable}='{serverUrlText}' is not a valid http(s) URL.");
        }

        var name = environment.GetValueOrDefault(WorkerNameVariable)?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            name = Environment.MachineName;
        }

        if (name.Length > 200)
        {
            return WorkerConfigurationResult.Invalid(
                $"configuration error: {WorkerNameVariable} is longer than 200 characters.");
        }

        var heartbeatSecondsText = environment.GetValueOrDefault(HeartbeatSecondsVariable)?.Trim();
        int heartbeatSeconds;
        if (string.IsNullOrEmpty(heartbeatSecondsText))
        {
            heartbeatSeconds = DefaultHeartbeatSeconds;
        }
        else if (!int.TryParse(heartbeatSecondsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out heartbeatSeconds)
            || heartbeatSeconds < 1)
        {
            return WorkerConfigurationResult.Invalid(
                $"configuration error: {HeartbeatSecondsVariable}='{heartbeatSecondsText}' is not a positive whole number of seconds.");
        }

        var tags = environment.GetValueOrDefault(WorkerTagsVariable)?.Trim() ?? string.Empty;
        if (tags.Length > 400)
        {
            return WorkerConfigurationResult.Invalid(
                $"configuration error: {WorkerTagsVariable} is longer than 400 characters.");
        }

        var registrationToken = environment.GetValueOrDefault(RegistrationTokenVariable)?.Trim();

        var dataDirectory = environment.GetValueOrDefault(DataDirectoryVariable)?.Trim();
        if (string.IsNullOrEmpty(dataDirectory))
        {
            dataDirectory = DefaultDataDirectory;
        }

        var workRoot = environment.GetValueOrDefault(WorkRootVariable)?.Trim();
        if (string.IsNullOrEmpty(workRoot))
        {
            workRoot = DefaultWorkRoot;
        }

        var cleanupTempText = environment.GetValueOrDefault(CleanupTempVariable)?.Trim();
        bool cleanupTemp;
        if (string.IsNullOrEmpty(cleanupTempText))
        {
            cleanupTemp = DefaultCleanupTemp;
        }
        else if (!bool.TryParse(cleanupTempText, out cleanupTemp))
        {
            return WorkerConfigurationResult.Invalid(
                $"configuration error: {CleanupTempVariable}='{cleanupTempText}' is not 'true' or 'false'.");
        }

        var concurrencyText = environment.GetValueOrDefault(WorkerConcurrencyVariable)?.Trim();
        int maxConcurrency;
        if (string.IsNullOrEmpty(concurrencyText))
        {
            maxConcurrency = DefaultMaxConcurrency;
        }
        else if (!int.TryParse(concurrencyText, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxConcurrency)
            || maxConcurrency < MinMaxConcurrency
            || maxConcurrency > MaxMaxConcurrency)
        {
            return WorkerConfigurationResult.Invalid(
                $"configuration error: {WorkerConcurrencyVariable}='{concurrencyText}' is not a whole number "
                + $"between {MinMaxConcurrency} and {MaxMaxConcurrency}.");
        }

        var claimSecondsText = environment.GetValueOrDefault(ClaimSecondsVariable)?.Trim();
        double claimSeconds;
        if (string.IsNullOrEmpty(claimSecondsText))
        {
            claimSeconds = DefaultClaimSeconds;
        }
        else if (!double.TryParse(claimSecondsText, NumberStyles.Float, CultureInfo.InvariantCulture, out claimSeconds)
            || claimSeconds < MinClaimSeconds
            || claimSeconds > MaxClaimSeconds)
        {
            return WorkerConfigurationResult.Invalid(
                $"configuration error: {ClaimSecondsVariable}='{claimSecondsText}' is not a number of seconds "
                + $"between {MinClaimSeconds.ToString(CultureInfo.InvariantCulture)} and "
                + $"{MaxClaimSeconds.ToString(CultureInfo.InvariantCulture)}.");
        }

        var logBufferLinesText = environment.GetValueOrDefault(LogBufferLinesVariable)?.Trim();
        int logBufferLines;
        if (string.IsNullOrEmpty(logBufferLinesText))
        {
            logBufferLines = DefaultLogBufferLines;
        }
        else if (!int.TryParse(logBufferLinesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out logBufferLines)
            || logBufferLines < MinLogBufferLines
            || logBufferLines > MaxLogBufferLines)
        {
            return WorkerConfigurationResult.Invalid(
                $"configuration error: {LogBufferLinesVariable}='{logBufferLinesText}' is not a whole number "
                + $"between {MinLogBufferLines} and {MaxLogBufferLines}.");
        }

        // F-013: the Git knobs. 0 means a full clone; a shallow clone is opt-in per deployment.
        var gitDepthText = environment.GetValueOrDefault(GitDepthVariable)?.Trim();
        int gitDepth;
        if (string.IsNullOrEmpty(gitDepthText))
        {
            gitDepth = DefaultGitDepth;
        }
        else if (!int.TryParse(gitDepthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out gitDepth)
            || gitDepth < MinGitDepth
            || gitDepth > MaxGitDepth)
        {
            return WorkerConfigurationResult.Invalid(
                $"configuration error: {GitDepthVariable}='{gitDepthText}' is not a whole number between "
                + $"{MinGitDepth} (full clone) and {MaxGitDepth}.");
        }

        var gitKnownHostsPath = environment.GetValueOrDefault(GitKnownHostsVariable)?.Trim();

        var gitTimeoutText = environment.GetValueOrDefault(GitTimeoutSecondsVariable)?.Trim();
        int gitTimeoutSeconds;
        if (string.IsNullOrEmpty(gitTimeoutText))
        {
            gitTimeoutSeconds = (int)DefaultGitOperationTimeout.TotalSeconds;
        }
        else if (!int.TryParse(gitTimeoutText, NumberStyles.Integer, CultureInfo.InvariantCulture, out gitTimeoutSeconds)
            || gitTimeoutSeconds < MinGitTimeoutSeconds
            || gitTimeoutSeconds > MaxGitTimeoutSeconds)
        {
            return WorkerConfigurationResult.Invalid(
                $"configuration error: {GitTimeoutSecondsVariable}='{gitTimeoutText}' is not a whole number "
                + $"between {MinGitTimeoutSeconds} and {MaxGitTimeoutSeconds}.");
        }

        return WorkerConfigurationResult.Valid(new WorkerConfiguration(
            serverUrl,
            string.IsNullOrEmpty(registrationToken) ? null : registrationToken,
            name,
            tags,
            TimeSpan.FromSeconds(heartbeatSeconds),
            dataDirectory,
            workRoot,
            cleanupTemp,
            maxConcurrency,
            TimeSpan.FromSeconds(claimSeconds),
            logBufferLines,
            gitDepth,
            gitKnownHostsPath,
            TimeSpan.FromSeconds(gitTimeoutSeconds)));
    }

    /// <summary>Builds the registration payload the server expects (PRD §3.4.2 fields).</summary>
    public WorkerRegistrationRequest BuildRegistrationRequest() => new(
        WorkerName,
        PlatformName(),
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        Version,
        Tags,
        Environment.ProcessorCount,
        MaxConcurrency);

    /// <summary>The same capabilities, refreshed on every heartbeat.</summary>
    public WorkerHeartbeatRequest BuildHeartbeatRequest() => new(
        PlatformName(),
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        Version,
        Tags,
        Environment.ProcessorCount,
        MaxConcurrency);

    /// <summary>
    /// The per-run Git layer's options for one run's work directory (card F-013). F-014 builds a
    /// <see cref="Git.GitWorkspace"/> from this at accept time; nothing here clones anything by itself.
    /// </summary>
    /// <param name="work">The run's prepared directory trio.</param>
    /// <param name="runDeadline">The run's <c>DeadlineAt</c>, so no invocation outlives the run.</param>
    /// <param name="log">Where the git layer writes its (already redacted) lines.</param>
    public Git.GitWorkspaceOptions BuildGitWorkspaceOptions(
        RunWorkDirectory work,
        DateTimeOffset? runDeadline = null,
        Action<string>? log = null) =>
        new()
        {
            Work = work,
            Depth = GitDepth,
            KnownHostsPath = string.IsNullOrEmpty(GitKnownHostsPath) ? null : GitKnownHostsPath,
            OperationTimeout = GitOperationTimeout ?? DefaultGitOperationTimeout,
            RunDeadline = runDeadline,
            Log = log,
        };

    private static string PlatformName() =>
        OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsLinux() ? "Linux"
            : OperatingSystem.IsMacOS() ? "macOS"
            : "Unknown";

    private static string Version =>
        typeof(WorkerConfiguration).Assembly.GetName().Version is { } assemblyVersion
            ? $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}"
            : "0.0.0";
}

/// <summary>Outcome of configuration parsing.</summary>
public sealed record WorkerConfigurationResult(WorkerConfiguration? Configuration, string? Error)
{
    public static WorkerConfigurationResult Valid(WorkerConfiguration configuration) =>
        new(configuration, null);

    public static WorkerConfigurationResult Invalid(string error) => new(null, error);
}
