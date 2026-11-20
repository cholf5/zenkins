using Xunit;
using Zenkins.Server.Components.Shared;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Runs;

namespace Zenkins.Tests.RunUi;

/// <summary>
/// Card F-047's status poll: a read updates the last good run, a terminal run stops the poll, a
/// failed read keeps what was last good and asks the page to try again, and an in-flight read is
/// never started twice. The page owns the 1s cadence; this class owns the one decision it cannot
/// make from a render alone.
/// </summary>
[Trait("Category", "RunUi")]
public sealed class RunLiveRefresherTests
{
    [Fact]
    public async Task A_running_read_updates_the_last_good_run_and_continues()
    {
        var running = Run(BuildRunStatus.Running);
        var refresher = new RunLiveRefresher(_ => Task.FromResult<RunView?>(running));

        var keep = await refresher.TryRefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(keep);
        Assert.Same(running, refresher.LastGood);
        Assert.Null(refresher.LastError);
    }

    [Fact]
    public async Task A_terminal_read_stops_polling_and_does_not_read_again()
    {
        var reads = 0;
        var refresher = new RunLiveRefresher(_ =>
        {
            reads++;
            return Task.FromResult<RunView?>(Run(BuildRunStatus.Success));
        });

        Assert.False(await refresher.TryRefreshAsync(TestContext.Current.CancellationToken));
        Assert.False(await refresher.TryRefreshAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, reads);
        Assert.Equal(BuildRunStatus.Success, refresher.LastGood!.Status);
    }

    [Fact]
    public async Task A_terminal_run_applied_at_page_load_is_not_read_at_all()
    {
        var reads = 0;
        var refresher = new RunLiveRefresher(_ =>
        {
            reads++;
            return Task.FromResult<RunView?>(Run(BuildRunStatus.Failed));
        });
        refresher.Apply(Run(BuildRunStatus.Canceled));

        Assert.False(await refresher.TryRefreshAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task A_failed_read_keeps_the_last_good_run_and_keeps_polling()
    {
        var good = Run(BuildRunStatus.Running);
        var fail = true;
        var refresher = new RunLiveRefresher(_ => fail
            ? Task.FromException<RunView?>(new InvalidOperationException("database is busy"))
            : Task.FromResult<RunView?>(good));
        refresher.Apply(good);

        Assert.True(await refresher.TryRefreshAsync(TestContext.Current.CancellationToken));
        Assert.Same(good, refresher.LastGood);
        Assert.NotNull(refresher.LastError);

        fail = false;
        Assert.True(await refresher.TryRefreshAsync(TestContext.Current.CancellationToken));
        Assert.Same(good, refresher.LastGood);
        Assert.Null(refresher.LastError);
    }

    [Fact]
    public async Task An_in_flight_read_is_not_started_twice()
    {
        var reads = 0;
        var release = new TaskCompletionSource<RunView?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresher = new RunLiveRefresher(_ =>
        {
            Interlocked.Increment(ref reads);
            return release.Task;
        });

        var first = refresher.TryRefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(await refresher.TryRefreshAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, Volatile.Read(ref reads));

        release.SetResult(Run(BuildRunStatus.Running));
        Assert.True(await first);
    }

    private static RunView Run(string status) => new(
        Id: Guid.NewGuid(),
        ProjectId: Guid.NewGuid(),
        Status: status,
        TriggerType: BuildRunTriggerType.Manual,
        TriggerUserEmail: null,
        WorkerName: null,
        PipelinePath: string.Empty,
        GitBranch: string.Empty,
        GitCommit: null,
        Pipeline: null,
        RequiredTags: [],
        StartedAt: null,
        EndedAt: null,
        ExitCode: null,
        FailureReason: null,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow);
}
