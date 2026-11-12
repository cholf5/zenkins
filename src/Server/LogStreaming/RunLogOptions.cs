using Microsoft.Extensions.Configuration;

namespace Zenkins.Server.LogStreaming;

/// <summary>
/// Knobs for card F-008, read from configuration once at startup (same shape as
/// <see cref="Zenkins.Server.Runs.RunOptions"/>).
/// </summary>
/// <param name="FlushLineCount">
/// How many buffered lines trigger an immediate database append (and how many a burst is split
/// into). This is the server-side memory bound of the ingest: no per-run buffer ever holds more
/// than this many lines beyond the flush call's own chunk.
/// </param>
/// <param name="FlushIntervalMilliseconds">
/// How long a partial buffer waits before it is flushed anyway - a quiet run's last lines must
/// reach the database (and the browser) without waiting for the next full batch.
/// </param>
/// <param name="HistoryChunkSize">
/// How many history lines one <c>HistoryLines</c> message carries when a browser joins a run
/// group. The database read is streamed; the chunk bounds the message size, not the log.
/// </param>
/// <param name="TicketLifetime">
/// How long a run-log subscription ticket stays valid. Matched to the authentication cookie's
/// eight-hour window: a page open across a long build must not have its subscription expire
/// mid-build, and the ticket is worthless beyond the session it was minted in.
/// </param>
public sealed record RunLogOptions(
    int FlushLineCount,
    int FlushIntervalMilliseconds,
    int HistoryChunkSize,
    TimeSpan TicketLifetime)
{
    public const string FlushLineCountConfigurationKey = "Logs:FlushLineCount";
    public const string FlushIntervalMillisecondsConfigurationKey = "Logs:FlushIntervalMilliseconds";
    public const string HistoryChunkSizeConfigurationKey = "Logs:HistoryChunkSize";
    public const string TicketLifetimeSecondsConfigurationKey = "Logs:TicketLifetimeSeconds";

    public static readonly RunLogOptions Defaults = new(
        FlushLineCount: 100,
        FlushIntervalMilliseconds: 250,
        HistoryChunkSize: 500,
        TicketLifetime: TimeSpan.FromHours(8));

    public static RunLogOptions FromConfiguration(IConfiguration configuration)
    {
        var flushLineCount = ReadInt(configuration, FlushLineCountConfigurationKey, Defaults.FlushLineCount, 1, 100_000);
        var flushInterval = ReadInt(
            configuration, FlushIntervalMillisecondsConfigurationKey, Defaults.FlushIntervalMilliseconds, 10, 60_000);
        var historyChunkSize = ReadInt(
            configuration, HistoryChunkSizeConfigurationKey, Defaults.HistoryChunkSize, 1, 10_000);
        var ticketLifetimeSeconds = ReadInt(
            configuration, TicketLifetimeSecondsConfigurationKey, (int)Defaults.TicketLifetime.TotalSeconds, 1, 86400);
        return new RunLogOptions(
            flushLineCount,
            flushInterval,
            historyChunkSize,
            TimeSpan.FromSeconds(ticketLifetimeSeconds));
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback, int min, int max)
    {
        var text = configuration[key];
        return int.TryParse(text, out var parsed) && parsed >= min && parsed <= max ? parsed : fallback;
    }
}
