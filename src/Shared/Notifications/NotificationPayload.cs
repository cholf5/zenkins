using System.Text.Json.Serialization;

namespace Zenkins.Shared.Notifications;

/// <summary>
/// The closed vocabulary of an outbound completion notification (card F-039): the three events a project can
/// subscribe to, plus the <c>all</c> subscription word.
/// <para>
/// <b>Three events, derived from the terminal state - not a field on the status.</b> <c>failed</c> is a run that
/// ended <c>Failed</c>; <c>success</c> is a run that ended <c>Success</c> and whose previous run on the same branch
/// did not fail; <c>recovered</c> is a run that ended <c>Success</c> whose previous run on the same branch ended
/// <c>Failed</c> or <c>Lost</c>. A run therefore carries exactly one event, and "back to green" is its own event
/// because it is the one people actually watch.
/// </para>
/// </summary>
public static class NotificationEvents
{
    /// <summary>The run ended <c>Failed</c>.</summary>
    public const string Failed = "failed";

    /// <summary>The run ended <c>Success</c> and the previous run of the same branch did not fail.</summary>
    public const string Success = "success";

    /// <summary>The run ended <c>Success</c> after a failure on the same branch: "back to green".</summary>
    public const string Recovered = "recovered";

    /// <summary>The subscription word that matches every event.</summary>
    public const string All = "all";

    /// <summary>What a subscription may name, in the order the form offers them.</summary>
    public static readonly string[] Subscribable = [Failed, Success, Recovered, All];

    /// <summary>
    /// The event a "send test" delivery carries. Never staged by a run and never subscribable: a receiver that sees it
    /// is looking at a configuration check, not at a build.
    /// </summary>
    public const string Test = "test";

    /// <summary>What a delivered payload can carry (never <see cref="All"/>: that is a subscription, not an event).</summary>
    public static readonly string[] Derived = [Failed, Success, Recovered];

    /// <summary>Whether <paramref name="value"/> is something a project may subscribe to.</summary>
    public static bool IsSubscribable(string? value) =>
        value is not null && Array.IndexOf(Subscribable, value) >= 0;

    /// <summary>
    /// Whether a comma-separated subscription list wants <paramref name="eventName"/>.
    /// <para>
    /// <b>A <c>success</c> subscription also matches <c>recovered</c>.</b> A recovery <em>is</em> a success - a chat
    /// bridge that asked for successes and silently dropped the recovery would tell the wrong story at exactly the
    /// moment people look. <c>recovered</c> alone matches only recoveries, and <c>all</c> matches everything.
    /// </para>
    /// </summary>
    public static bool Matches(string subscriptions, string eventName)
    {
        ArgumentNullException.ThrowIfNull(eventName);
        foreach (var subscription in Split(subscriptions))
        {
            if (subscription == All
                || subscription == eventName
                || (subscription == Success && eventName == Recovered))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Canonical form of a submitted subscription list: known words only, deduplicated, in
    /// <see cref="Subscribable"/>'s order. Empty or null means "no event", which the caller refuses.
    /// </summary>
    public static string? Normalize(string? subscriptions)
    {
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Split(subscriptions))
        {
            if (!IsSubscribable(entry))
            {
                return null;
            }

            chosen.Add(entry);
        }

        if (chosen.Count == 0)
        {
            return null;
        }

        return string.Join(",", Subscribable.Where(chosen.Contains));
    }

    private static IEnumerable<string> Split(string? subscriptions) =>
        string.IsNullOrWhiteSpace(subscriptions)
            ? []
            : subscriptions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// One completion notification's body (card F-039): the stable, documented shape a receiver parses, pinned by the
/// tests and by <c>docs/notifications.md</c>.
/// <para>
/// <b>Nothing here is a secret, a parameter or an environment snapshot.</b> The fields are the run's identity, the
/// project's identity, the terminal facts a reader needs and the two timestamps; a parameter value could be a
/// password (F-041) and an environment variable could be a token, so neither is in the contract at all. That is a
/// property of the type: there is no field to leak them into.
/// </para>
/// </summary>
/// <param name="Event">One of <see cref="NotificationEvents.Derived"/>.</param>
/// <param name="DeliveryId">
/// The delivery's stable id across retries (the outbox row id). A receiver that gets the same id twice has seen a
/// retry, not a second run - this is the value in the <c>X-Zenkins-Delivery</c> header.
/// </param>
/// <param name="Attempt">1 for the first send, 2 for the first retry, and so on.</param>
public sealed record NotificationPayload(
    string Event,
    string DeliveryId,
    int Attempt,
    NotificationRun Run,
    NotificationProject Project,
    string? FailureReason,
    DateTimeOffset FinishedAt,
    DateTimeOffset SentAt);

/// <summary>One run, as a notification describes it.</summary>
/// <param name="Number">Card F-036's build number inside the project ("#12").</param>
/// <param name="Url">
/// Where a person can read the run: absolute when the Server knows its own public base URL
/// (<c>Notifications:PublicBaseUrl</c>), otherwise the relative path - honest about not knowing rather than
/// inventing a host.
/// </param>
/// <param name="RebuiltFromRunNumber">Card F-037: the run this one rebuilt or replayed, when it still resolves.</param>
public sealed record NotificationRun(
    Guid Id,
    int Number,
    string Branch,
    string? Commit,
    string Status,
    string TriggerType,
    string Url,
    Guid? RebuiltFromRunId = null,
    int? RebuiltFromRunNumber = null);

/// <summary>One project, as a notification describes it.</summary>
public sealed record NotificationProject(Guid Id, string Name, string Url);

/// <summary>
/// The serializer context for <see cref="NotificationPayload"/> (camelCase, generated - no reflection), so the
/// Worker/Server/NativeAOT builds all produce the same bytes and the signature covers exactly what was sent.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NotificationPayload))]
public sealed partial class NotificationJsonContext : JsonSerializerContext;
