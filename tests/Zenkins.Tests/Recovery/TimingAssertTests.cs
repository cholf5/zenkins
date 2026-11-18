using Xunit;

namespace Zenkins.Tests.Recovery;

/// <summary>
/// Card Q-023: the two timing questions, pinned by name so a later "make the waits longer" change cannot
/// quietly turn a "stays true" property into a "was true once" one.
/// </summary>
public sealed class TimingAssertTests
{
    [Fact]
    public async Task A_hold_fails_at_the_first_sample_that_does_not_hold()
    {
        // The shape a load-induced flake has: the property holds, stops holding for one sample, and holds
        // again. A "green if it ever held" implementation passes this; the real one must fail it.
        var samples = 0;

        var failure = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => TimingAssert.AssertHoldsAsync(
            _ =>
            {
                samples++;
                if (samples == 2)
                {
                    throw new InvalidOperationException("the worker came back online");
                }

                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(3),
            "the worker stays Offline",
            TestContext.Current.CancellationToken));

        Assert.Contains("the worker stays Offline", failure.Message, StringComparison.Ordinal);
        Assert.Contains("did not hold after", failure.Message, StringComparison.Ordinal);
        Assert.Contains("3.0s budget", failure.Message, StringComparison.Ordinal);
        Assert.Contains("the worker came back online", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hold_that_never_violates_returns_without_spending_more_than_its_budget()
    {
        var samples = 0;

        await TimingAssert.AssertHoldsAsync(
            _ =>
            {
                samples++;
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(300),
            "nothing happens",
            TestContext.Current.CancellationToken);

        Assert.True(samples > 0, "the hold took no sample at all.");
    }

    [Fact]
    public void A_call_sites_lambda_reads_as_one_sentence_in_the_failure()
    {
        // Card Q-023 (AC-2): the description is the call site's own expression - which names the exact
        // assertions - but it has to read like prose, not like source.
        var normalized = TimingAssert.NormalizeDescription(
            "async () => {\n            var run = await host.ReadRunAsync(heldRunId);\n"
            + "            Assert.Equal(\"Running\", run.Status);\n        }");

        Assert.Equal(
            "var run = await host.ReadRunAsync(heldRunId); Assert.Equal(\"Running\", run.Status)",
            normalized);

        // A hand-written sentence passes through untouched - that is the override path.
        Assert.Equal(
            "the run stays Running while the lease holds",
            TimingAssert.NormalizeDescription("the run stays Running while the lease holds"));

        // An over-long body is truncated so the message stays readable.
        var longText = TimingAssert.NormalizeDescription("x => " + new string('a', 400));
        Assert.True(longText.Length <= 143, $"the description is {longText.Length} characters.");
        Assert.EndsWith("...", longText, StringComparison.Ordinal);

        // Nothing at all still says something true.
        Assert.Equal("the asserted property holds", TimingAssert.NormalizeDescription(null));
    }

    [Fact]
    public async Task A_hold_with_no_budget_fails_instead_of_passing_on_no_evidence()
    {
        // A zero (or negative) budget used to make the loop fall through without sampling: green, and about
        // nothing. The hold now refuses that shape outright - "it held" for zero milliseconds is not a claim.
        var failure = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => TimingAssert.AssertHoldsAsync(
            _ => Task.CompletedTask,
            TimeSpan.Zero,
            "a window with no budget",
            TestContext.Current.CancellationToken));

        Assert.Contains("cannot be asserted with a budget of 0 ms", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hold_whose_samples_do_not_span_the_window_fails()
    {
        // The other half of the same guard: enough samples is not enough if they all happen at the start.
        var failure = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => TimingAssert.AssertHoldsAsync(
            _ =>
            {
                // One sample that outlives the whole budget: the loop cannot take a second one.
                Thread.Sleep(30);
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(5),
            "a window sampled only once",
            TestContext.Current.CancellationToken));

        Assert.Contains("hold needs at least two", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wait_that_never_holds_fails_naming_what_it_waited_for_and_how_long()
    {
        var failure = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => TimingAssert.WaitUntilAsync(
            _ => throw new InvalidOperationException("still Queued"),
            TimeSpan.FromMilliseconds(300),
            "the run starts Running",
            TestContext.Current.CancellationToken));

        Assert.Contains("the run starts Running", failure.Message, StringComparison.Ordinal);
        Assert.Contains("was not satisfied within 0.3s", failure.Message, StringComparison.Ordinal);
        Assert.Contains("still Queued", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wait_returns_as_soon_as_the_condition_holds()
    {
        var attempts = 0;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        await TimingAssert.WaitUntilAsync(
            _ =>
            {
                attempts++;
                if (attempts < 2)
                {
                    throw new InvalidOperationException("not yet");
                }

                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(30),
            "the second attempt is enough",
            TestContext.Current.CancellationToken);
        elapsed.Stop();

        Assert.Equal(2, attempts);

        // The budget is 30s; the condition held on the second poll. A wait that sleeps out its budget would
        // return here too - only the clock tells the two apart.
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(3),
            $"the wait took {elapsed.Elapsed.TotalSeconds:F1}s of a 30s budget for a condition that held on "
            + "the second poll - it slept instead of returning.");
    }
}
