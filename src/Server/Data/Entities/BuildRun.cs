using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data.Entities;

/// <summary>
/// The well-known build-run states (PRD §3.5). Stored as a string so the model stays
/// provider-neutral; the check constraint in <see cref="ZenkinsDbContext"/> keeps out-of-band
/// writes honest. Transitions between states are validated in exactly one place
/// (<c>BuildRunStateMachine</c>); illegal transitions are a conflict (409).
/// </summary>
public static class BuildRunStatus
{
    /// <summary>Waiting for an eligible worker to claim it (or for its lease to expire).</summary>
    public const string Queued = "Queued";

    /// <summary>Accepted by a worker and executing there.</summary>
    public const string Running = "Running";

    /// <summary>Finished with exit code 0. Terminal.</summary>
    public const string Success = "Success";

    /// <summary>Finished with a non-zero exit code. Terminal.</summary>
    public const string Failed = "Failed";

    /// <summary>Canceled by a user before or during execution (F-009). Terminal.</summary>
    public const string Canceled = "Canceled";

    /// <summary>The worker disappeared without reporting a result (F-010). Terminal.</summary>
    public const string Lost = "Lost";
}

/// <summary>How a run entered the queue. An open set: new trigger sources add values.</summary>
public static class BuildRunTriggerType
{
    /// <summary>Triggered by an organization member from the project page (this card).</summary>
    public const string Manual = "Manual";

    /// <summary>
    /// Triggered by a signed webhook delivery (card F-017): a `git push` to a configured branch. Such a
    /// run has no <see cref="BuildRun.TriggerUserId"/> - nobody was signed in - and carries the pushed
    /// commit from the payload instead of waiting for the worker's accept report.
    /// </summary>
    public const string Webhook = "Webhook";

    /// <summary>
    /// Triggered by an organization API token (card F-027): a script sent the trigger with a token's
    /// <c>trigger</c> scope. Such a run has no <see cref="BuildRun.TriggerUserId"/> - nobody was signed
    /// in - and is otherwise the same run a member's click queues (the same validation, gate and queue).
    /// </summary>
    public const string Api = "Api";

    /// <summary>
    /// Card F-037's <b>Rebuild</b>: a member re-ran a finished run with the same inputs - the branch,
    /// the commit the original run pinned, its parameters, tags and timeout, and the definition that
    /// original executed. <see cref="BuildRun.RebuiltFromRunId"/> names the run it came from. It is an
    /// ordinary queued run in every other respect: the same F-028/F-029 gate, the same queue and the
    /// same numbering as a fresh trigger.
    /// </summary>
    public const string Rebuild = "Rebuild";

    /// <summary>
    /// Card F-037's <b>Replay</b>: a member re-ran a finished run against the definition text that run
    /// itself carried, ignoring whatever the project or the repository says now. Distinct from
    /// <see cref="Rebuild"/> on purpose - replaying a repository pipeline would pretend the frozen spec
    /// is the source that produced it, so today only a run whose definition <em>is</em> the stored text
    /// (a Freestyle run) can be replayed, and the run page says why when it cannot.
    /// </summary>
    public const string Replay = "Replay";

    /// <summary>
    /// Card F-024's scheduled trigger: the cron clock queued the run with the definitions' defaults. Like a
    /// webhook delivery nobody was signed in, so it carries no <see cref="BuildRun.TriggerUserId"/>; in every
    /// other respect it is the run a member's click queues - the same F-028/F-029 gate, the same queue, the
    /// same build numbering, the same worker contract.
    /// </summary>
    public const string Schedule = "Schedule";

    /// <summary>
    /// Card F-025's SCM poll: the Server's poller saw the branch head move and queued a run pinned to the
    /// observed commit - the same pinned-commit run a webhook delivery queues, for hosts that cannot reach
    /// the Server. Nobody was signed in, so it carries no <see cref="BuildRun.TriggerUserId"/>.
    /// </summary>
    public const string Poll = "Poll";
}

/// <summary>
/// One execution of a project's command (PRD §3.5, card F-006). Tenant-owned like every other
/// organization row. A v0.2 run is a manually specified argv command executed inside the worker's
/// per-run work directory (F-007) - <c>Pipeline*</c> fields stay out until Phase 3 and Git fields
/// until Phase 4.
/// <para>
/// <b>Claim/lease.</b> A claim does not change <see cref="Status"/>: it stamps
/// <see cref="WorkerId"/> and <see cref="LeaseExpiresAt"/> while the run stays
/// <see cref="BuildRunStatus.Queued"/>. The worker must report <c>accepted</c> inside the lease
/// window; the lease sweeper requeues an expired, never-accepted lease. After <c>accepted</c>
/// there is no lease - <see cref="LeaseExpiresAt"/> is cleared and cancellation (F-009) and
/// offline detection (F-010) own the rest.
/// </para>
/// </summary>
public sealed class BuildRun : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The run's build number inside its project (card F-036): a positive integer assigned at trigger
    /// time, displayed as <c>#N</c> everywhere a person reads it and used by the permalinks
    /// (<c>/projects/{id}/runs/{number}</c>).
    /// <para>
    /// <b>Per project, and atomic.</b> The unique index on <c>(ProjectId, Number)</c> is the guard, not a
    /// pre-flight check: the staging routine reads <c>MAX(Number) + 1</c> while it holds the project row
    /// lock (the F-028/F-029 gate's own lock), so two concurrent triggers of one project serialize and a
    /// lost race is retried once against the index rather than silently reusing a number. Numbers are
    /// never reused and never renumbered: a run keeps the number it was queued with even after the queue
    /// is cancelled or the project's limits change.
    /// </para>
    /// <para>
    /// <b>Backfilled.</b> Runs that predate the column were numbered by the migration in creation order
    /// (<c>CreatedAt</c>, then <c>Id</c>), deterministically, per project - the migration and its ordering
    /// are the only writer that ever assigns a number outside the trigger path.
    /// </para>
    /// </summary>
    public int Number { get; set; }

    /// <inheritdoc />
    public Guid OrganizationId { get; set; }

    /// <summary>The project this run executes for. Cascade-deleted with it (F-003 defers the
    /// cascade decision to this card: build history is not meaningful without its project).</summary>
    public Guid ProjectId { get; set; }

    public Project Project { get; set; } = null!;

    public string Status { get; set; } = BuildRunStatus.Queued;

    /// <summary>The <see cref="BuildRunTriggerType"/> discriminator.</summary>
    public required string TriggerType { get; set; }

    /// <summary>
    /// The Identity user id of the member who triggered the run (manual triggers). Deliberately no
    /// foreign key: build history must survive account deletion.
    /// </summary>
    public string? TriggerUserId { get; set; }

    /// <summary>Comma-separated tags a claimant worker must cover (empty when unrestricted).</summary>
    public required string RequiredTags { get; set; } = string.Empty;

    /// <summary>
    /// The project's pipeline file, relative to the repository root, snapshotted when the run was
    /// triggered (card F-014). A snapshot, not a join: editing the project later must not change where
    /// an existing run looked for its definition - and a historical run must stay readable after the
    /// project is edited.
    /// </summary>
    public required string PipelinePath { get; set; }

    /// <summary>
    /// The branch this run builds (card F-014): the trigger form's branch, or the project's default
    /// branch when the form left it blank. Also a snapshot - the project's default may move later.
    /// </summary>
    public required string GitBranch { get; set; }

    /// <summary>The worker the run is leased to (claim) or executing on (accepted). Null when
    /// queued and never claimed.</summary>
    public Guid? WorkerId { get; set; }

    public Worker? Worker { get; set; }

    /// <summary>When the current lease expires; null when the run is not leased. Cleared on
    /// acceptance - a running run has no lease.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>When the run is accepted (Queued → Running).</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>
    /// The exact commit this run builds: an object name (hex sha), never a branch label - a branch moves,
    /// and the point of the column is to say what this run actually built.
    /// <para>
    /// <b>Two sources, one invariant.</b> A manual run starts empty and is filled from the worker's
    /// definition report (<c>git rev-parse HEAD</c> after its own clone, card F-013). A webhook run
    /// (card F-017) is created with the pushed <c>after</c>, and the claim then hands that same commit to
    /// the worker with the instruction to pin it (review M1) - so the recorded commit and the checked-out
    /// tree are the same fact even when the branch moves between the push and the clone. For a run that
    /// already carries one, a later report naming a different commit is refused
    /// (<c>RunErrorCodes.GitCommitMismatch</c>, 409): the column is never rewritten and a disagreement is
    /// never silently absorbed.
    /// </para>
    /// <para>
    /// <b>Written once.</b> The stamp is a guarded compare-and-set on <c>GitCommit IS NULL</c>
    /// (<c>ZenkinsDbContext.TryStampGitCommitAsync</c>), so a second report - or a re-delivery of the same
    /// one - cannot rewrite what the run built.
    /// </para>
    /// </summary>
    public string? GitCommit { get; set; }

    /// <summary>
    /// The inline Pipeline run's script, snapshotted at trigger time (card F-022). The same discipline
    /// <see cref="PipelinePath"/> follows: editing the project afterwards never changes a queued or
    /// running run. Null for a repository run (whose definition is the checkout's file) and for
    /// Freestyle (whose definition is <see cref="PipelineSpecJson"/>).
    /// <para>
    /// The Worker writes this text to <c>work/{run-id}/temp/pipeline.lua</c> and runs the definition
    /// phase against it, so the diagnostic is <c>pipeline.lua:(line,col)</c> - exactly a repository
    /// pipeline's - and the frozen spec it reports is the one execution contract either way. The claim
    /// carries the same value; this column is what makes the snapshot durable across a worker restart.
    /// </para>
    /// </summary>
    public string? PipelineScriptText { get; set; }

    /// <summary>
    /// The frozen, validated pipeline spec (card F-014): the JSON the worker produced from the
    /// definition phase and the server stored verbatim.
    /// <para>
    /// <b>Written once, then authoritative.</b> The definition report is the only writer and the
    /// compare-and-set in <c>ZenkinsDbContext.FreezePipelineDefinitionAsync</c> refuses a second write,
    /// so what the run executed is exactly what is stored. The run page renders the step list from it,
    /// which is why a run that is only just starting already shows its steps. Null until the worker
    /// reported the definition (a queued run, or a run that failed before defining anything).
    /// </para>
    /// </summary>
    public string? PipelineSpecJson { get; set; }

    /// <summary>
    /// The resolved build-parameter values of this run (card F-021): a JSON object of parameter name
    /// → value, snapshotted at trigger time (a later definition edit never moves a queued run) and
    /// handed to the worker in the claim payload, where it joins the server-provided environment
    /// layer. Null when the project declares no parameters.
    /// </summary>
    public string? ParametersJson { get; set; }

    /// <summary>
    /// The organization environment layer this run executes with (card F-034): a JSON object of
    /// variable name → value, frozen at trigger time exactly like the branch, the spec and the
    /// parameter values. Null when the organization defined no variables when this run was queued.
    /// <para>
    /// <b>Its own column, not part of <see cref="ParametersJson"/>.</b> The parameter snapshot has a
    /// second consumer with its own meaning - the trigger form of a rebuild/replay re-resolves it
    /// against today's definitions, which refuses a name the project never declared - so merging the
    /// organization layer into it would make a rebuild post configuration as parameters. The two
    /// layers travel together (one environment block in the claim, one composition on the worker) but
    /// are stored, and read back to the page, under their own names.
    /// </para>
    /// </summary>
    public string? EnvironmentJson { get; set; }

    /// <summary>Set when the run reaches a terminal state. Duration is derived on display.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>The process exit code reported by the worker (Success ⇔ 0).</summary>
    public int? ExitCode { get; set; }

    /// <summary>Machine-readable reason for an abnormal end (canceled-by-user, timeout,
    /// worker-offline). Null for a plain non-zero exit - the exit code is the reason.</summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Per-run timeout override in seconds (the trigger form collects minutes); <c>null</c> when
    /// the trigger used the server default (<c>Runs:DefaultTimeoutMinutes</c>, F-009). The
    /// effective timeout is frozen into <see cref="DeadlineAt"/> at accept time, so a later
    /// configuration change never moves an existing run's deadline.
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// The execution deadline, frozen at accept time: <c>StartedAt + effective timeout</c> where
    /// the effective timeout is this run's <see cref="TimeoutSeconds"/> override or the server
    /// default. Null until accepted (a queued run has no deadline). The deadline sweeper
    /// (F-009) never touches a run before this instant.
    /// </summary>
    public DateTimeOffset? DeadlineAt { get; set; }

    /// <summary>
    /// Set when a cancellation was requested for the still-running run (F-009): a user cancel
    /// (<see cref="CancelReason"/> = <c>canceled-by-user …</c>) or the deadline sweeper's timeout
    /// request. The worker kills the process tree on the signal and reports through the
    /// <c>canceled</c> endpoint; until that report the run stays <c>Running</c>.
    /// </summary>
    public DateTimeOffset? CancelRequestedAt { get; set; }

    /// <summary>Why the cancellation was requested (<c>canceled-by-user &lt;user&gt;</c> or
    /// <c>timeout</c>, F-009). Becomes the run's <see cref="FailureReason"/> when the worker
    /// reports the kill - the server, not the worker, owns the wording.</summary>
    public string? CancelReason { get; set; }

    /// <summary>
    /// Card F-037: the finished run this one re-executed (a rebuild or a replay), or <see langword="null"/>.
    /// Deliberately <b>no foreign key</b>: deleting a project cascades its runs away, and a surviving
    /// rebuild must not lose its history to the original's disappearance. The run page renders
    /// "rebuilt from #N" by resolving this id to the original's <see cref="Number"/> at read time, and
    /// falls back to the short id when the original is gone.
    /// </summary>
    public Guid? RebuiltFromRunId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
