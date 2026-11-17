using Microsoft.Extensions.Options;
using Zenkins.Shared.Pipelines;

namespace Zenkins.Server.Probes;

/// <summary>
/// The knobs of a definition-only validation (card F-022), the same shapes the Git probe's caller wait
/// has: how long a requesting page waits for a Worker, how long a claim's lease holds, and how often the
/// waiter polls. Every value has a production default and is configuration-overridable so a test can
/// compress time without faking it.
/// </summary>
public sealed class DefinitionValidationOptions
{
    public const string WaitSecondsKey = "DefinitionValidation:WaitSeconds";
    public const string LeaseSecondsKey = "DefinitionValidation:LeaseSeconds";
    public const string PollMillisecondsKey = "DefinitionValidation:PollMilliseconds";

    /// <summary>How long the Validate request waits for a Worker before answering "no worker".</summary>
    public TimeSpan Wait { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How long one claim holds before the Server may offer it to another Worker.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the waiter re-reads the registry.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Reads the three knobs from configuration, falling back to the production defaults.</summary>
    public static DefinitionValidationOptions FromConfiguration(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var options = new DefinitionValidationOptions();

        if (double.TryParse(read(WaitSecondsKey), out var wait) && wait > 0)
        {
            options.Wait = TimeSpan.FromSeconds(wait);
        }

        if (double.TryParse(read(LeaseSecondsKey), out var lease) && lease > 0)
        {
            options.Lease = TimeSpan.FromSeconds(lease);
        }

        if (double.TryParse(read(PollMillisecondsKey), out var poll) && poll > 0)
        {
            options.PollInterval = TimeSpan.FromMilliseconds(poll);
        }

        return options;
    }
}
