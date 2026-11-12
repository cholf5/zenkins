namespace Zenkins.Shared.LogStreaming;

/// <summary>
/// The machine-readable rejection protocol of the worker's upstream log hub (card F-008, review
/// fix F1). A server-side refusal that can never succeed on retry - unknown or foreign run, run
/// assigned to a different worker, dead worker token, connection without a worker credential -
/// throws a <c>HubException</c> whose message carries <see cref="Prefix"/>. The worker's drain
/// classifies a completion by that machine prefix instead of parsing prose.
/// <para>
/// Everything else stays retryable: a transport failure throws something else entirely, and a
/// non-<c>HubException</c> server fault arrives as the framework's generic "An unexpected error
/// occurred invoking ... on the server." completion - no prefix, so the drain keeps the lines
/// and waits for the reconnect. The wording-based discriminator this replaces (a substring of
/// SignalR's generic completion message) never matched the forwarded rejection texts at all,
/// which is exactly the bug this protocol exists to fix.
/// </para>
/// <para>
/// The prefix is matched by <em>containment</em>, not by a start-of-message check: ASP.NET Core
/// 10's hub dispatcher wraps a hub-method failure as "An unexpected error occurred invoking 'X'
/// on the server. <c>HubException:</c> <em>message</em>" when detailed errors are off, so the
/// prefix arrives in the middle of the completion message. The prefix is machine-namespaced
/// ("zenkins:rejected:"), and the framework's own wrapper text can never contain it, so
/// containment cannot produce a false positive - and it stays stable whether the message is
/// forwarded verbatim, wrapped, or delivered with detailed errors enabled.
/// </para>
/// </summary>
public static class LogStreamRejection
{
    /// <summary>The stable prefix every definitive log-stream rejection carries.</summary>
    public const string Prefix = "zenkins:rejected:";

    /// <summary>Builds the wire message for one definitive rejection reason.</summary>
    public static string For(string reason) => $"{Prefix} {reason}";

    /// <summary>Whether a completion message is a definitive (never-retryable) rejection. Matched
    /// by containment: the stable machine prefix must survive the framework's error wrapping
    /// ("An unexpected error occurred invoking 'X' on the server. HubException: …") intact - see
    /// the type's doc.</summary>
    public static bool IsRejection(string? message) =>
        message is not null && message.Contains(Prefix, StringComparison.Ordinal);
}
