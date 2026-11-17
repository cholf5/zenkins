using Zenkins.Shared.Projects;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// A CI project owned by an organization (PRD §3.2, §3.10). The <see cref="Kind"/> decides where a
/// run's definition comes from: a <see cref="ProjectKinds.Pipeline"/> project names a Git repository
/// whose <c>pipeline.lua</c> is read by the Worker's definition phase; a
/// <see cref="ProjectKinds.Freestyle"/> project needs no repository - its steps are configured in the
/// UI, frozen into <see cref="FreestyleSpecJson"/>, and every run travels with that snapshot.
/// </summary>
public sealed class Project : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// The project's kind (card F-020): <see cref="ProjectKinds.Pipeline"/> or
    /// <see cref="ProjectKinds.Freestyle"/>. Data, not a type hierarchy - the frozen spec stays the one
    /// execution contract for both.
    /// </summary>
    public string Kind { get; set; } = ProjectKinds.Pipeline;

    /// <summary>
    /// Where a <see cref="ProjectKinds.Pipeline"/> project's definition lives (card F-022): a Git
    /// repository (<see cref="ProjectSources.Repository"/>, the original source) or a Lua body stored
    /// on this row (<see cref="ProjectSources.Inline"/>). <b>Immutable after create</b>, exactly like
    /// <see cref="Kind"/>: a run snapshots its definition shape, and letting the source move would strand
    /// queued runs. A Freestyle project never reads this column and keeps the historical default.
    /// </summary>
    public string PipelineSource { get; set; } = ProjectSources.Repository;

    /// <summary>
    /// The inline Pipeline's Lua body (card F-022), bounded by
    /// <see cref="Zenkins.Shared.Pipelines.PipelineScriptLimits.MaxScriptBytes"/>. Null for a
    /// repository project - there the definition is the repository's file - and null for Freestyle,
    /// whose definition is <see cref="FreestyleSpecJson"/>. The script is copied onto every run at
    /// trigger time; editing it here never moves a queued or running run.
    /// </summary>
    public string? InlineScript { get; set; }

    /// <summary>
    /// The repository Pipeline projects clone (nullable since F-020: a Freestyle project has none).
    /// <see langword="null"/> never reaches a clone - the Freestyle path skips it.
    /// </summary>
    public string? RepositoryUrl { get; set; }

    /// <summary>The branch Pipeline projects build by default; <see langword="null"/> for Freestyle.</summary>
    public string? DefaultBranch { get; set; }

    /// <summary>
    /// Card F-023: how this project decides which branches it builds - one job on the default branch
    /// (<see cref="Zenkins.Shared.Projects.BranchDiscoveryModes.Single"/>, the default and today's
    /// behaviour) or one branch job per discovered branch
    /// (<see cref="Zenkins.Shared.Projects.BranchDiscoveryModes.Branches"/>).
    /// <para>
    /// <b>A setting, not a kind.</b> Only a repository Pipeline can discover branches: Freestyle and
    /// inline projects have no repository, and the validator refuses <c>branches</c> for them rather
    /// than storing a setting nothing can honour. The column is mutable because switching is the point
    /// of the card - and switching never rewrites history: the branch list unions the stored branch jobs
    /// with the branches existing runs already snapshot. The migration adds the column with a
    /// <c>'single'</c> default, so every row written before this card answers <c>single</c> from the
    /// database itself, not only from this initializer.
    /// </para>
    /// </summary>
    public string BranchDiscovery { get; set; } = Zenkins.Shared.Projects.BranchDiscoveryModes.Single;

    /// <summary>
    /// Path of the pipeline file inside the repository; PRD §3.2 default is <c>pipeline.lua</c>.
    /// <see langword="null"/> for Freestyle (no file, no path).
    /// </summary>
    public string? PipelinePath { get; set; } = "pipeline.lua";

    /// <summary>
    /// The project's build-parameter definitions (card F-021, PRD §3.11): a JSON array of
    /// <see cref="Zenkins.Shared.Projects.ProjectParameter"/>, serialized with the source-generated
    /// <c>ProjectParameterJsonContext</c>. Null means "no parameters declared"; the trigger form
    /// renders them for both kinds, and a run snapshots the resolved values.
    /// </summary>
    public string? ParametersJson { get; set; }

    /// <summary>
    /// The Freestyle project's frozen step spec (card F-020): the same
    /// <see cref="Zenkins.Shared.Pipelines.PipelineSpec"/> JSON the Worker freezes for a Pipeline run,
    /// built and validated by the Server at save time and copied onto every run at trigger time. Null
    /// for Pipeline projects and for a Freestyle project whose steps were never saved yet.
    /// </summary>
    public string? FreestyleSpecJson { get; set; }

    /// <summary>
    /// Reference to the Git credential this project's runs clone with (card F-018 writes it). The
    /// credential is a <see cref="GitCredential"/> row of the same organization; <see langword="null"/>
    /// means <b>no credential is assigned</b>, and a run then clones without one instead of borrowing
    /// whatever credential the worker host happens to hold.
    /// </summary>
    public Guid? GitCredentialId { get; set; }

    /// <summary>
    /// When the current assignment was made (card F-018's audit trail). Cleared on detach, so a
    /// non-null value always describes <see cref="GitCredentialId"/> - never a previous credential.
    /// </summary>
    public DateTimeOffset? GitCredentialAssignedAt { get; set; }

    /// <summary>
    /// Who made the current assignment: the acting user's id, resolved to an e-mail for display.
    /// Like <c>GitCredential.CreatedByUserId</c> it carries no foreign key, so deleting an account
    /// never rewrites a project's history.
    /// </summary>
    public string? GitCredentialAssignedByUserId { get; set; }

    /// <summary>
    /// Card F-028: the reversible "stop every trigger" switch, the project-level analogue of
    /// <see cref="Worker.IsDisabled"/>. A disabled project queues nothing from any trigger source, its
    /// history, artifacts and settings stay readable, and enabling clears the state - it is not a soft
    /// delete and stores no deleted marker (<see cref="DisabledReason"/> is the operator's own words,
    /// not a tombstone).
    /// </summary>
    public bool Disabled { get; set; }

    /// <summary>
    /// Why the project was disabled (F-028): required when disabling, cleared when enabling. The refused
    /// trigger surfaces it verbatim, so the person who pushes or clicks Start reads the operator's
    /// reason instead of a bare code.
    /// </summary>
    public string? DisabledReason { get; set; }

    /// <summary>When the current disable happened (F-028); cleared with <see cref="Disabled"/>.</summary>
    public DateTimeOffset? DisabledAt { get; set; }

    /// <summary>
    /// Who disabled the project (F-028): the acting user's id, resolved to an e-mail for display. Like
    /// <c>GitCredential.CreatedByUserId</c> it carries no foreign key, so deleting an account never
    /// rewrites a project's history.
    /// </summary>
    public string? DisabledByUserId { get; set; }

    /// <summary>
    /// Card F-029: the most runs of this project that may be active at once - <c>Running</c>, plus
    /// claimed-but-not-yet-accepted runs (the same "assigned" predicate the worker cap counts), so a
    /// claim cannot outrun the limit through a lease. <see langword="null"/> means unlimited, which is
    /// what every project created before this card has.
    /// </summary>
    public int? MaxConcurrentRuns { get; set; }

    /// <summary>
    /// Card F-029: seconds a freshly queued run waits before it becomes claimable (Jenkins' quiet
    /// period), measured from <see cref="BuildRun.CreatedAt"/>. Zero - the default - means claimable
    /// immediately. Triggers inside the period are ordinary queued runs; nothing is coalesced.
    /// </summary>
    public int QuietPeriodSeconds { get; set; }

    /// <summary>
    /// Card F-029: the most runs this project may have <c>Queued</c>. A trigger at the cap is refused
    /// <em>before</em> any run row is written. <see langword="null"/> means unlimited.
    /// </summary>
    public int? MaxQueuedRuns { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Card I-005: the outcome of the project's most recent Git remote probe - a
    /// <see cref="Zenkins.Shared.Probes.GitProbeOutcomes"/> value, or <see langword="null"/> for a project
    /// that was never probed.
    /// <para>
    /// <b>This is the "last outcome on the project" the card requires.</b> The probe itself is transient
    /// (in-memory, see the probe registry), but a failure must stay visible for a person reading the
    /// project page - and must never be mistaken for "no change" (which is why the value is the outcome
    /// word, not a boolean or a ref count). Null says "never probed", which is distinct from a recorded
    /// failure.
    /// </para>
    /// </summary>
    public string? LastProbeOutcome { get; set; }

    /// <summary>When the most recent probe settled (I-005); null when none ever did.</summary>
    public DateTimeOffset? LastProbeAt { get; set; }

    /// <summary>
    /// The most recent probe's diagnostic words (a failure's stderr tail, or the Server's "nobody
    /// claimed it" sentence), bounded like the column. Null when there was nothing to say.
    /// </summary>
    public string? LastProbeMessage { get; set; }
}
