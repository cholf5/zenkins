using System.Reflection;
using Xunit;
using Zenkins.Server.Branches;
using Zenkins.Server.Data.Entities;
using Zenkins.Shared.Projects;

namespace Zenkins.Tests.Branches;

/// <summary>
/// The coupling that makes card F-025's branch-verdict gate safe, pinned so it cannot be broken silently.
/// <para>
/// <b>Two questions, one field.</b> A poll asks for a branch's verdict exactly when
/// <see cref="BranchDiscoveryModes.IsMultibranch"/> says the project is multibranch; the discovery pass
/// selects exactly the projects <see cref="BranchDiscoverySweeper.Selects"/> accepts. Today both read the
/// same <c>Project.BranchDiscovery</c> value, which is why a poll's <c>awaiting-observation</c> always has
/// somebody who will resolve it: the sweeper observes the same repository, the verdict becomes
/// <c>has-pipeline</c>, and the next tick queues the run.
/// </para>
/// <para>
/// <b>What breaks if they drift.</b> Give discovery its own switch - the obvious next step - and a project
/// can poll without anybody ever reading its tree: every tick records <c>awaiting-observation</c>, no run is
/// ever queued for a branch that does carry a <c>pipeline.lua</c>, and nothing reports it. A dead end with
/// no failure to notice. This fact is the tripwire: the two answers must be identical for <b>every</b> mode,
/// and a new mode has to be classified rather than defaulted.
/// </para>
/// </summary>
public sealed class BranchDiscoveryCouplingTests
{
    [Fact]
    public void Every_discovery_mode_answers_the_poll_gate_and_the_discovery_pass_the_same_way()
    {
        // A mode that exists but is missing from All would be a default rather than a decision - the poll
        // would read it as single-branch while discovery, or a future selector, might not.
        var declared = typeof(BranchDiscoveryModes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(declared, BranchDiscoveryModes.All.OrderBy(value => value, StringComparer.Ordinal).ToArray());

        foreach (var mode in BranchDiscoveryModes.All)
        {
            var pollAsksForAVerdict = BranchDiscoveryModes.IsMultibranch(mode);
            var discoverySelectsTheProject = BranchDiscoverySweeper.Selects(new Project
            {
                Name = $"coupling-{mode}",
                BranchDiscovery = mode,
            });

            Assert.True(
                pollAsksForAVerdict == discoverySelectsTheProject,
                $"Branch discovery mode '{mode}': the poll asks for a branch verdict ({pollAsksForAVerdict}) "
                + $"but the discovery pass selects the project ({discoverySelectsTheProject}). A poll's "
                + "awaiting-observation would then have nobody who resolves it - no run for a branch that "
                + "does carry a pipeline, and no failure to notice.");
        }

        // The two concrete answers, so a future failure reads as "single" or "branches" instead of a bare
        // boolean: both sides must say yes to every branch's verdict, and fine to say no to one branch only.
        Assert.True(BranchDiscoveryModes.IsMultibranch(BranchDiscoveryModes.Branches));
        Assert.True(BranchDiscoverySweeper.Selects(new Project
        {
            Name = "coupling-branches",
            BranchDiscovery = BranchDiscoveryModes.Branches,
        }));

        Assert.False(BranchDiscoveryModes.IsMultibranch(BranchDiscoveryModes.Single));
        Assert.False(BranchDiscoverySweeper.Selects(new Project
        {
            Name = "coupling-single",
            BranchDiscovery = BranchDiscoveryModes.Single,
        }));
    }
}
