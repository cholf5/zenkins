using Xunit;
using Zenkins.Server.Branches;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Webhooks;
using Zenkins.Shared.Projects;

namespace Zenkins.Tests.Branches;

/// <summary>
/// Card F-023's pure rules: the discovery mode vocabulary, the branch identity bound, the orphan
/// evidence words, and the two branch-filter rules that must never be confused - F-017's three-argument
/// <c>Matches</c> (empty filter = the project's <em>current</em> default branch) and F-023's discovery
/// rule (empty filter = every branch). No host, no database.
/// </summary>
[Trait("Category", "Branches")]
public sealed class BranchRulesTests
{
    [Fact]
    public void The_discovery_mode_is_a_closed_vocabulary_with_single_as_the_default()
    {
        Assert.Equal(BranchDiscoveryModes.Single, BranchDiscoveryModes.Normalize(null));
        Assert.Equal(BranchDiscoveryModes.Single, BranchDiscoveryModes.Normalize(string.Empty));
        Assert.Equal(BranchDiscoveryModes.Single, BranchDiscoveryModes.Normalize("main"));
        Assert.Equal(BranchDiscoveryModes.Branches, BranchDiscoveryModes.Normalize("branches"));

        Assert.True(BranchDiscoveryModes.IsKnown("single"));
        Assert.True(BranchDiscoveryModes.IsKnown("branches"));
        Assert.False(BranchDiscoveryModes.IsKnown("Branch"));
        Assert.True(BranchDiscoveryModes.IsMultibranch("branches"));
        Assert.False(BranchDiscoveryModes.IsMultibranch("single"));
        Assert.False(BranchDiscoveryModes.IsMultibranch(null));
    }

    [Fact]
    public void A_branch_identity_is_never_empty_and_never_longer_than_the_run_column()
    {
        // The run's GitBranch column holds 200 characters while a probe may report a 255-character ref,
        // so 200 is the one bound both sides share. An empty name is the product's Freestyle marker,
        // not a branch.
        Assert.True(BranchIdentity.IsStorable("main"));
        Assert.True(BranchIdentity.IsStorable(new string('a', BranchIdentity.MaxLength)));
        Assert.False(BranchIdentity.IsStorable(new string('a', BranchIdentity.MaxLength + 1)));
        Assert.False(BranchIdentity.IsStorable(string.Empty));
        Assert.False(BranchIdentity.IsStorable(null));
        Assert.Equal(200, BranchIdentity.MaxLength);
    }

    [Fact]
    public void The_pipeline_state_is_a_closed_vocabulary_and_only_no_pipeline_blocks_queueing()
    {
        Assert.Equal(
            [BranchPipelineStates.Unknown, BranchPipelineStates.HasPipeline, BranchPipelineStates.NoPipeline],
            BranchPipelineStates.All);

        Assert.True(BranchPipelineStates.IsBuildable(BranchPipelineStates.Unknown));
        Assert.True(BranchPipelineStates.IsBuildable(BranchPipelineStates.HasPipeline));
        Assert.False(BranchPipelineStates.IsBuildable(BranchPipelineStates.NoPipeline));
        Assert.False(BranchPipelineStates.IsKnown("missing"));
    }

    [Fact]
    public void The_orphan_evidence_is_a_closed_vocabulary_of_positive_observations()
    {
        // Only evidence that something looked and found the branch gone: a delete event the sender
        // signed, or a successful refs answer that no longer lists it. There is deliberately no
        // "probe-failed" word - a failure is not proof of absence.
        Assert.Equal([BranchOrphanEvidence.PushDelete, BranchOrphanEvidence.RefsAbsent], BranchOrphanEvidence.All);
        Assert.True(BranchOrphanEvidence.IsKnown("push-delete"));
        Assert.True(BranchOrphanEvidence.IsKnown("refs-absent"));
        Assert.False(BranchOrphanEvidence.IsKnown("git-unreachable"));
        Assert.False(BranchOrphanEvidence.IsKnown("no-worker"));
    }

    [Fact]
    public void The_discovery_rule_treats_an_empty_filter_as_every_branch_not_as_the_default_branch()
    {
        // F-023's rule: a multibranch project discovers everything unless the webhook narrows it.
        Assert.True(WebhookBranchFilter.MatchesDiscovery(string.Empty, "main"));
        Assert.True(WebhookBranchFilter.MatchesDiscovery(null, "feature/x"));
        Assert.True(WebhookBranchFilter.MatchesDiscovery("release/*", "release/1.2"));
        Assert.False(WebhookBranchFilter.MatchesDiscovery("release/*", "main"));
        Assert.True(WebhookBranchFilter.MatchesDiscovery("main,release/*", "main"));
        Assert.False(WebhookBranchFilter.MatchesDiscovery("main,release/*", "feature/x"));
    }

    [Fact]
    public void The_f017_three_argument_rule_still_means_the_current_default_branch()
    {
        // The regression target: discovery's "every branch" must not leak into the single-branch rule.
        Assert.True(WebhookBranchFilter.Matches(string.Empty, "main", defaultBranch: "main"));
        Assert.False(WebhookBranchFilter.Matches(string.Empty, "feature/x", defaultBranch: "main"));
        Assert.True(WebhookBranchFilter.Matches(string.Empty, "trunk", defaultBranch: "trunk"));
        Assert.True(WebhookBranchFilter.Matches("release/*", "release/1.2", defaultBranch: "main"));
        Assert.False(WebhookBranchFilter.Matches("release/*", "main", defaultBranch: "main"));
    }
}
