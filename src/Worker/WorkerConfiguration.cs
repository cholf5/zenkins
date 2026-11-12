using System.Globalization;

namespace Zenkins.Worker;

/// <summary>
/// The parsed and validated worker configuration, read from environment variables
/// (card F-005). Validation happens once at startup: invalid configuration produces a clear
/// one-line error and a non-zero exit - never a stack trace.
/// <para>
/// <c>ZENKINS_DATA_DIR</c> is an extra knob beyond the card's table: it overrides the directory the
/// worker token is persisted under (default: <c>.zenkins</c> inside the user profile). It exists so
/// the process-level E2E can run without touching the real user profile, and so operators can place
/// the credential on a mounted secret volume.
/// </para>
/// </summary>
public sealed record WorkerConfiguration(
    Uri ServerUrl,
    string? RegistrationToken,
    string WorkerName,
    string Tags,
    TimeSpan HeartbeatInterval,
    string DataDirectory)
{
    public const string ServerUrlVariable = "ZENKINS_SERVER_URL";
    public const string RegistrationTokenVariable = "ZENKINS_REGISTRATION_TOKEN";
    public const string WorkerNameVariable = "ZENKINS_WORKER_NAME";
    public const string WorkerTagsVariable = "ZENKINS_WORKER_TAGS";
    public const string HeartbeatSecondsVariable = "ZENKINS_HEARTBEAT_SECONDS";
    public const string DataDirectoryVariable = "ZENKINS_DATA_DIR";

    /// <summary>PRD §3.4.5: default heartbeat interval and maximum concurrent tasks of 1.</summary>
    public const int DefaultHeartbeatSeconds = 15;
    public const int MaxConcurrency = 1;

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

        return WorkerConfigurationResult.Valid(new WorkerConfiguration(
            serverUrl,
            string.IsNullOrEmpty(registrationToken) ? null : registrationToken,
            name,
            tags,
            TimeSpan.FromSeconds(heartbeatSeconds),
            dataDirectory));
    }

    /// <summary>Builds the registration payload the server expects (PRD §3.4.2 fields).</summary>
    public WorkerRegistrationRequest BuildRegistrationRequest() => new(
        WorkerName,
        OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsLinux() ? "Linux"
            : OperatingSystem.IsMacOS() ? "macOS"
            : "Unknown",
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        Version,
        Tags,
        Environment.ProcessorCount,
        MaxConcurrency);

    /// <summary>The same capabilities, refreshed on every heartbeat.</summary>
    public WorkerHeartbeatRequest BuildHeartbeatRequest() => new(
        OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsLinux() ? "Linux"
            : OperatingSystem.IsMacOS() ? "macOS"
            : "Unknown",
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        Version,
        Tags,
        Environment.ProcessorCount,
        MaxConcurrency);

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
