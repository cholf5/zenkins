using Xunit;
using Zenkins.Worker;

namespace Zenkins.Tests.Workers;

/// <summary>
/// Card F-038's worker half: the age-based sweep of <c>work/{run-id}</c> trees. It is the only code that can remove
/// a run's checkout, published artifact copies and scratch space from a worker's disk - the Server cannot reach it -
/// so its three rules are pinned here: old trees go, young trees and live runs stay, and nothing that is not a run
/// tree is touched.
/// </summary>
[Trait("Category", "Workers")]
public sealed class WorkDirectorySweeperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "zenkins-work-sweep-" + Guid.NewGuid().ToString("N"));

    public WorkDirectorySweeperTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void An_old_run_tree_is_removed_and_a_young_one_is_kept()
    {
        var oldRun = NewRunTree(daysOld: 10);
        var youngRun = NewRunTree(daysOld: 1);
        var sweeper = CreateSweeper(hours: 168);

        var result = sweeper.SweepOnce(DateTimeOffset.UtcNow);

        Assert.Equal([Guid.Parse(Path.GetFileName(oldRun))], result.Removed);
        Assert.False(Directory.Exists(oldRun), "the old run tree survived the sweep");
        Assert.True(Directory.Exists(youngRun), "the young run tree was swept");
        Assert.Equal(1, result.Fresh);

        // Idempotent: the same clock over the same disk removes nothing more.
        Assert.Empty(sweeper.SweepOnce(DateTimeOffset.UtcNow).Removed);
    }

    [Fact]
    public void A_run_this_worker_is_executing_is_never_removed_even_when_its_tree_looks_old()
    {
        var liveRun = NewRunTree(daysOld: 30);
        var activity = new RunActivityRegistry();
        activity.Begin(Guid.Parse(Path.GetFileName(liveRun)));
        var sweeper = CreateSweeper(hours: 1, activity: activity);

        var result = sweeper.SweepOnce(DateTimeOffset.UtcNow);

        Assert.Empty(result.Removed);
        Assert.Equal(1, result.Active);
        Assert.True(Directory.Exists(liveRun), "a live run's work tree was removed");

        // ...and once the run is out of the active set, the same tree is fair game.
        activity.End(Guid.Parse(Path.GetFileName(liveRun)));
        Assert.Single(sweeper.SweepOnce(DateTimeOffset.UtcNow).Removed);
        Assert.False(Directory.Exists(liveRun));
    }

    [Fact]
    public void Only_run_trees_are_swept()
    {
        // A probe's and a validation's scratch trees live under the same root and have their own cleanup; an
        // operator's stray directory is not ours to delete either.
        var probes = Path.Combine(_root, "probes", Guid.NewGuid().ToString("D"));
        var validations = Path.Combine(_root, "validations", Guid.NewGuid().ToString("D"));
        var stray = Path.Combine(_root, "operator-notes");
        foreach (var directory in new[] { probes, validations, stray })
        {
            Directory.CreateDirectory(directory);
            Age(directory, daysOld: 90);
        }

        var sweeper = CreateSweeper(hours: 1);
        Assert.Empty(sweeper.SweepOnce(DateTimeOffset.UtcNow).Removed);
        Assert.True(Directory.Exists(probes));
        Assert.True(Directory.Exists(validations));
        Assert.True(Directory.Exists(stray));
    }

    [Fact]
    public void A_zero_window_turns_the_sweep_off()
    {
        var oldRun = NewRunTree(daysOld: 365);
        var sweeper = CreateSweeper(hours: 0);

        Assert.False(sweeper.Enabled);
        Assert.Empty(sweeper.SweepOnce(DateTimeOffset.UtcNow).Removed);
        Assert.True(Directory.Exists(oldRun), "the disabled sweep removed a tree");
    }

    private string NewRunTree(int daysOld)
    {
        var runId = Guid.NewGuid();
        var directory = Path.Combine(_root, runId.ToString("D"));
        Directory.CreateDirectory(Path.Combine(directory, "repository"));
        Directory.CreateDirectory(Path.Combine(directory, "artifacts"));
        File.WriteAllText(Path.Combine(directory, "repository", "pipeline.lua"), "return {}");
        Age(directory, daysOld);
        return directory;
    }

    private static void Age(string path, int daysOld) =>
        Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysOld));

    private WorkDirectorySweeper CreateSweeper(int hours, RunActivityRegistry? activity = null)
    {
        var configuration = WorkerConfiguration.FromEnvironment(new Dictionary<string, string?>
        {
            [WorkerConfiguration.ServerUrlVariable] = "https://zenkins.example.test",
            [WorkerConfiguration.WorkRetentionHoursVariable] = hours.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Configuration!;

        return new WorkDirectorySweeper(
            new WorkDirectoryService(_root),
            activity ?? new RunActivityRegistry(),
            configuration,
            TextWriter.Null);
    }
}
