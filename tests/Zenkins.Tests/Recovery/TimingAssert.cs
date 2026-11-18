using System.Diagnostics;

namespace Zenkins.Tests.Recovery;

/// <summary>
/// Card Q-023: the timing assertions, in one place, with a failure that says what it was waiting for.
/// <para>
/// <b>Two different questions, two different shapes.</b> <see cref="WaitUntilAsync"/> asks "does this become
/// true within the budget" (a condition that is expected to change) and returns as soon as it does.
/// <see cref="AssertHoldsAsync"/> asks "does this stay true for the whole budget" (a property that must not
/// change) and therefore fails at the <b>first</b> sample that does not hold - it must never be implemented as
/// "it was true at least once", because that would turn "the worker stays Offline" and "the run stays
/// unclaimed" into statements about a single instant.
/// </para>
/// <para>
/// <b>Why the budget is not a knob to raise.</b> A longer budget does not make a busy machine's race go away;
/// it makes the red rarer and the suite slower. The budget here exists so a failure can report it, not so a
/// wait can be extended until it passes.
/// </para>
/// </summary>
internal static class TimingAssert
{
    /// <summary>How long between samples of a hold, and between polls of a condition.</summary>
    public static readonly TimeSpan Sample = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Fails unless <paramref name="sample"/> passes on <b>every</b> sample taken during
    /// <paramref name="budget"/>. The first failure is reported with the description, the elapsed time and the
    /// budget - "it did not hold" without those three cannot be acted on.
    /// </summary>
    public static async Task AssertHoldsAsync(
        Func<CancellationToken, Task> sample,
        TimeSpan budget,
        string description,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var stopwatch = Stopwatch.StartNew();
        var samples = 0;
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;
        while (true)
        {
            try
            {
                await sample(cancellationToken);
                samples++;
                first ??= DateTimeOffset.UtcNow;
                last = DateTimeOffset.UtcNow;
            }
            catch (Exception failure)
            {
                throw new Xunit.Sdk.XunitException(
                    $"'{description}' did not hold after {stopwatch.Elapsed.TotalSeconds:F1}s of a "
                    + $"{budget.TotalSeconds:F1}s budget: {failure.Message}");
            }

            // Sleep only as far as the budget allows, so the last sample lands ON the deadline: a hold that
            // stopped sampling at 2/3 of its window would be a statement about that moment, not the window.
            var remaining = budget - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                AssertHeldAcross(description, samples, first, last, budget);
                return;
            }

            await Task.Delay(remaining < Sample ? remaining : Sample, cancellationToken);
        }
    }

    /// <summary>
    /// The tail of <see cref="AssertHoldsAsync"/>: a hold is a statement about a <b>window</b>, so its
    /// evidence has to span that window - at least two samples, and a first-to-last span of at least 80% of
    /// the budget. Without this, "it held" can be a statement about a single instant (or about nothing at
    /// all, when the budget is zero), which is exactly the failure a load-sensitive suite produces.
    /// </summary>
    /// <summary>
    /// Turns a call site's lambda expression into one readable sentence: whitespace collapsed, the
    /// <c>async () =&gt;</c> arrow and its braces stripped, and an over-long body truncated. The lambda text is a
    /// better description than a hand-written sentence (it names the exact assertions), but it has to read
    /// like prose in a failure message, not like source code.
    /// </summary>
    public static string NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return "the asserted property holds";
        }

        var text = string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        foreach (var prefix in new[] { "async () =>", "() =>", "async _ =>", "_ =>" })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text = text[prefix.Length..].Trim();
                break;
            }
        }

        if (text.StartsWith('{'))
        {
            text = text[1..];
        }

        if (text.EndsWith('}'))
        {
            text = text[..^1];
        }

        text = text.Trim().TrimEnd(';').Trim();

        const int maxLength = 140;
        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd() + "...";
    }

    internal static void AssertHeldAcross(
        string description,
        int samples,
        DateTimeOffset? first,
        DateTimeOffset? last,
        TimeSpan budget)
    {
        if (budget <= TimeSpan.Zero)
        {
            throw new Xunit.Sdk.XunitException(
                $"'{description}' cannot be asserted with a budget of {budget.TotalMilliseconds:F0} ms.");
        }

        if (samples < 2)
        {
            throw new Xunit.Sdk.XunitException(
                $"'{description}' took {samples} sample(s) in a {budget.TotalMilliseconds:F0} ms budget - a "
                + "hold needs at least two to say anything about a window.");
        }

        var span = last - first;
        var required = TimeSpan.FromTicks((long)(budget.Ticks * 0.8));
        if (span is null || span < required)
        {
            throw new Xunit.Sdk.XunitException(
                $"'{description}' sampled {first?.ToString("O") ?? "never"} to {last?.ToString("O") ?? "never"} "
                + $"({span?.TotalMilliseconds:F0} ms) inside a {budget.TotalMilliseconds:F0} ms budget - the "
                + "samples must cover at least 80% of the window.");
        }
    }

    /// <summary>
    /// Waits until <paramref name="condition"/> is satisfied, or fails with what was waited for, how long it
    /// waited and the budget it had. Returns as soon as it holds - a condition that becomes true in 50ms does
    /// not spend the rest of the budget sleeping.
    /// </summary>
    public static async Task WaitUntilAsync(
        Func<CancellationToken, Task> condition,
        TimeSpan budget,
        string description,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var stopwatch = Stopwatch.StartNew();
        Exception? last = null;
        while (stopwatch.Elapsed < budget)
        {
            try
            {
                await condition(cancellationToken);
                return;
            }
            catch (Exception failure)
            {
                last = failure;
            }

            await Task.Delay(Sample, cancellationToken);
        }

        throw new Xunit.Sdk.XunitException(
            $"'{description}' was not satisfied within {budget.TotalSeconds:F1}s "
            + $"(waited {stopwatch.Elapsed.TotalSeconds:F1}s); last observed: {last?.Message ?? "no attempt"}");
    }
}
