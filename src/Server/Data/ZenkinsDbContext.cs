using System.Linq.Expressions;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Git;
using Zenkins.Server.Api;
using Zenkins.Server.Projects;
using Zenkins.Server.Secrets;
using Zenkins.Server.Artifacts;
using Zenkins.Server.Branches;
using Zenkins.Server.Environments;
using Zenkins.Server.Schedules;
using Zenkins.Server.Webhooks;
using Zenkins.Shared.LogStreaming;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Probes;
using Zenkins.Shared.Projects;
using Zenkins.Shared.Tenancy;

namespace Zenkins.Server.Data;

/// <summary>
/// The only EF Core context in the product. Tenant isolation is enforced here, not by callers:
/// query filters hide other organizations' rows and <c>SaveChanges</c> refuses cross-tenant writes.
/// <para>
/// <b>Bypasses this context cannot guard:</b> <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> never call
/// <c>SaveChanges</c> (the query filter only constrains their <c>WHERE</c>, not their <c>SET</c>), and
/// raw SQL does not go through the model at all. Do not use either on tenant-owned entities outside
/// an <see cref="ISystemDataScope"/> scope; add a guarded method here if one is ever needed.
/// </para>
/// <para>
/// <b>Tenant switches:</b> the change tracker is dropped whenever <see cref="ITenantContext.TenantRevision"/>
/// moves (and the switch is rejected outright if there are unsaved changes), because
/// <c>Find</c>/<c>Include</c> short-circuit on tracked rows without consulting the query filter. This is deliberately cast-free so a decorator or replacement
/// <see cref="ITenantContext"/> (F-002) keeps the protection. The check runs on <c>Set&lt;T&gt;()</c>,
/// <c>DbContext.Find</c>/<c>FindAsync</c> and <c>SaveChanges</c>; <c>ChangeTracker.Entries()</c>,
/// <c>Entry()</c> and <c>Local</c> are <b>not</b> guarded. The binding rule therefore stays
/// "one context per tenant, one context per operation"; the revision clear is defence in depth for
/// the residual case, not a licence to share a context across tenants.
/// </para>
/// <para>
/// <b>Lifetime:</b> register the context per operation, never as a long-lived object. UI components
/// must not inject <see cref="ZenkinsDbContext"/> (an interactive Blazor circuit lives for hours);
/// they use <see cref="IDbContextFactory{TContext}"/> and create one context per operation.
/// </para>
/// <para>
/// <b>Organizations:</b> inserts are intentionally unguarded - bootstrap (F-001) creates the first
/// organization before any tenant exists. Only modify/delete are constrained to the active
/// organization (or a system scope).
/// </para>
/// <para>
/// <b>Identity:</b> F-001 re-bases the model on <see cref="IdentityUserContext{TUser}"/> (users,
/// claims, logins and tokens - deliberately <b>not</b> Identity roles, because PRD §3.1.2 defines
/// Owner/Member per organization). The Identity entities are not tenant-owned, but
/// <see cref="OrganizationMember.UserId"/> carries a real foreign key to <c>AspNetUsers</c>.
/// </para>
/// </summary>
public sealed class ZenkinsDbContext : IdentityUserContext<User>
{
    private readonly ITenantContext _tenantContext;
    private readonly ISystemDataScope _systemDataScope;
    private int _observedTenantRevision;

    public ZenkinsDbContext(
        DbContextOptions<ZenkinsDbContext> options,
        ITenantContext tenantContext,
        ISystemDataScope systemDataScope)
        : base(options)
    {
        _tenantContext = tenantContext;
        _systemDataScope = systemDataScope;
        _observedTenantRevision = tenantContext.TenantRevision;
    }

    /// <summary>
    /// Active organization exposed as a plain property so query filters reference <em>this context
    /// instance</em> rather than a captured <see cref="ITenantContext"/>. Falls back to
    /// <see cref="Guid.Empty"/> (which matches nothing) when no tenant is selected, so a
    /// filter can never accidentally mean "all tenants".
    /// </summary>
    public Guid CurrentOrganizationId => _tenantContext.OrganizationId ?? Guid.Empty;

    /// <summary>
    /// Deliberately unfiltered: an organization must be readable while the tenant is still being
    /// selected (the organization switcher runs before a tenant exists). Writes are still guarded
    /// in <c>SaveChanges</c>: only the active organization may be modified or deleted.
    /// </summary>
    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<Worker> Workers => Set<Worker>();

    public DbSet<WorkerToken> WorkerTokens => Set<WorkerToken>();

    /// <summary>Build runs (F-006): queued/running/finished executions of a project's command.</summary>
    public DbSet<BuildRun> BuildRuns => Set<BuildRun>();

    /// <summary>Incrementally persisted output lines of build runs (F-008). Never queried without
    /// a run id: the composite (RunId, Seq) primary key orders every read.</summary>
    public DbSet<BuildLog> BuildLogs => Set<BuildLog>();

    /// <summary>Per-step records of a pipeline run (F-015): one row per step position, one per run.</summary>
    public DbSet<BuildStep> BuildSteps => Set<BuildStep>();

    /// <summary>Encrypted organization secrets (F-012). Write-only above this layer: nothing outside
    /// <c>SecretService.TryGetValueAsync</c> ever decrypts a row, and no endpoint returns a value.</summary>
    public DbSet<Secret> Secrets => Set<Secret>();

    /// <summary>Plain (unencrypted) organization environment variables (F-034): the layer every
    /// project's steps receive, snapshotted onto each run at trigger time.</summary>
    public DbSet<OrganizationEnvironmentVariable> OrganizationEnvironmentVariables =>
        Set<OrganizationEnvironmentVariable>();

    /// <summary>Encrypted Git credentials (F-013). Write-only above this layer: the only decrypt path
    /// is <c>GitCredentialService.FetchMaterialForRunAsync</c>, which serves one run's worker.</summary>
    public DbSet<GitCredential> GitCredentials => Set<GitCredential>();

    /// <summary>Organization API tokens (F-027): SHA-256 hashes, never plaintext; the hash lookup
    /// through the system scope is what selects the tenant for <c>/api/v1/*</c>.</summary>
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    /// <summary>Remembered <c>Idempotency-Key</c> values of API triggers (F-027), one per
    /// (token, key); the unique index is the replay guard.</summary>
    public DbSet<ApiIdempotencyRecord> ApiIdempotencyRecords => Set<ApiIdempotencyRecord>();

    /// <summary>First-run guard; at most one row ever exists (see <see cref="BootstrapMarker"/>).</summary>
    public DbSet<BootstrapMarker> BootstrapMarkers => Set<BootstrapMarker>();

    /// <summary>Per-project cron schedules (F-024): the expression, its zone and the overlap switch.</summary>
    public DbSet<ProjectSchedule> ProjectSchedules => Set<ProjectSchedule>();

    /// <summary>The due-time ledger of every schedule (F-024): one row per (schedule, due time), the
    /// record of what happened and the guard against queueing twice.</summary>
    public DbSet<ScheduleOccurrence> ScheduleOccurrences => Set<ScheduleOccurrence>();

    /// <summary>One row per branch a multibranch project tracks (F-023), including the branch seen and
    /// not built. Unique on (ProjectId, Branch).</summary>
    public DbSet<BranchJob> BranchJobs => Set<BranchJob>();

    // Every query starts by resolving a DbSet, so this is the one place that catches a tenant switch
    // before Find/Include can short-circuit on rows tracked under the previous tenant.
    public override DbSet<TEntity> Set<TEntity>()
        where TEntity : class
    {
        DropTrackerIfTenantChanged();
        return base.Set<TEntity>();
    }

    public override DbSet<TEntity> Set<TEntity>(string name)
        where TEntity : class
    {
        DropTrackerIfTenantChanged();
        return base.Set<TEntity>(name);
    }

    // The non-generic Find/FindAsync resolve through EF's entity finder and bypass Set<T>(), so they
    // need the sampler explicitly. (In EF Core 10 the generic Find<T>/FindAsync<T> do go through
    // Set<T>() - pinned by Generic_find_is_the_first_tracker_access_after_a_switch - but that is an
    // implementation detail, so the non-generic overloads carry their own guard rather than relying
    // on it.)
    public override object? Find(Type entityType, params object?[]? keyValues)
    {
        DropTrackerIfTenantChanged();
        return base.Find(entityType, keyValues);
    }

    public override ValueTask<object?> FindAsync(Type entityType, params object?[]? keyValues)
    {
        DropTrackerIfTenantChanged();
        return base.FindAsync(entityType, keyValues, CancellationToken.None);
    }

    public override ValueTask<object?> FindAsync(
        Type entityType,
        object?[]? keyValues,
        CancellationToken cancellationToken)
    {
        DropTrackerIfTenantChanged();
        return base.FindAsync(entityType, keyValues, cancellationToken);
    }

    /// <summary>
    /// Cross-tenant read path for system operations. The scope is checked when the query is
    /// <em>executed</em> and the rows are materialised before this method returns, because an
    /// <c>IQueryable</c> could be captured inside the scope and enumerated after it ended.
    /// </summary>
    public Task<List<TEntity>> SystemQueryAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(predicate);

        if (!_systemDataScope.IsActive)
        {
            throw new InvalidOperationException(
                $"Cross-tenant access to '{typeof(TEntity).Name}' requires an active {nameof(ISystemDataScope)} scope.");
        }

        return Set<TEntity>()
            .IgnoreQueryFilters()
            .Where(predicate)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// B-002: locks the claimant's own worker row for the duration of one claim - the write is a
    /// deliberate no-op (the column is set to its own value, so nothing changes) whose entire
    /// purpose is the lock itself. PostgreSQL takes the row's write lock until the caller commits;
    /// SQLite serializes database writes outright. Two concurrent claims of the same worker
    /// therefore serialize here instead of interleaving the slot count with the lease
    /// (check-then-act, the window B-002 closes), while claims of different workers never wait on
    /// each other beyond the brief transaction. A second guarded <c>ExecuteUpdate</c> on a
    /// tenant-owned entity outside a system scope (<see cref="TryLeaseBuildRunAsync"/>,
    /// <see cref="LockProjectForTriggerAsync"/> and
    /// <see cref="TryCancelQueuedProjectRunsAsync"/> are the others; see the class doc's bypass
    /// discipline): the SET is fixed, never caller-supplied, and the WHERE carries the tenant
    /// query filter.
    /// </summary>
    /// <returns>1 when the worker row was locked, 0 when it no longer exists.</returns>
    public Task<int> LockWorkerForClaimAsync(
        Guid workerId,
        CancellationToken cancellationToken = default) =>
        Workers
            .Where(worker => worker.Id == workerId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(worker => worker.UpdatedAt, worker => worker.UpdatedAt),
                cancellationToken);

    /// <summary>
    /// F-028 + F-029: locks one project row for the duration of a trigger or a claim - the same
    /// deliberate no-op write <see cref="LockWorkerForClaimAsync"/> uses, applied to the project row
    /// instead of the worker row. PostgreSQL takes the row's write lock until the caller commits;
    /// SQLite serializes database writes outright. Three decisions ride on this one lock, which is why
    /// they can be read and acted on atomically instead of check-then-act - but they are <b>not</b> read
    /// the same way, and the difference matters:
    /// <list type="bullet">
    /// <item><b>Trigger (insert path), F-028.</b> The staging routine re-reads <c>Projects.Disabled</c>
    /// <em>under</em> the lock, immediately before the insert, so a trigger that started before a disable
    /// committed cannot slip a run into a disabled project: the disable's own update is the same row
    /// lock, so the two serialize and the loser reads the winner's flag.</item>
    /// <item><b>Trigger (insert path), F-029.</b> The queue cap counts the project's queued runs under
    /// the same lock, so two concurrent triggers cannot both pass a "below the cap?" check.</item>
    /// <item><b>Claim path, F-029.</b> The claim counts the project's active runs under the lock, so two
    /// workers cannot both take the second slot of a <c>MaxConcurrentRuns = 1</c> project.</item>
    /// </list>
    /// <para>
    /// <b>The claim path does not re-read <c>Disabled</c> under this lock</b> - its candidate rows are a
    /// snapshot read before the lock, and its <c>Disabled</c> filter is therefore defence in depth, not
    /// the guarantee. What actually keeps a disabled project's runs from being built is a pair of
    /// statements that already serialize on the run rows: disabling cancels every <c>Queued</c> run in
    /// the same transaction as the flag, and the lease compare-and-set (F-006, unchanged) only matches
    /// <c>Status == Queued</c>. So a claim racing a disable either commits its lease first - and the
    /// disable's cancel then cancels that run, making the holder's <c>accepted</c> report the documented
    /// 409 - or finds the run already canceled and its CAS updates zero rows. No run of a disabled
    /// project reaches <c>Running</c> through either interleaving.
    /// </para>
    /// <para>
    /// <b>Lock order.</b> Callers take worker row -> project row -> run rows. Projects are attempted in
    /// the queue's own FIFO order (the candidate order is a subsequence of one global
    /// <c>(CreatedAt, Id)</c> order), so two claims never hold each other's next project: no cycle.
    /// </para>
    /// <para>
    /// A second guarded <c>ExecuteUpdate</c> on a tenant-owned entity outside a system scope (see the
    /// class doc's bypass discipline): the SET is fixed, never caller-supplied, and the WHERE carries
    /// the tenant query filter.
    /// </para>
    /// </summary>
    /// <returns>1 when the project row was locked, 0 when it no longer exists.</returns>
    public Task<int> LockProjectForTriggerAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        Projects
            .Where(project => project.Id == projectId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(project => project.UpdatedAt, project => project.UpdatedAt),
                cancellationToken);

    /// <summary>
    /// F-024: locks one schedule row for the duration of its evaluation - the same deliberate no-op write
    /// <see cref="LockProjectForTriggerAsync"/> performs on a project, applied to the schedule. Two schedulers
    /// (two Server instances, or two ticks of one) that both decide the same due time is due therefore
    /// serialize here: the second one re-reads the watermark the first committed and finds nothing left to do.
    /// The unique <c>(ScheduleId, DueAt)</c> index below it is the guard for any future caller that stages
    /// outside this lock.
    /// </summary>
    /// <returns>1 when the schedule row was locked, 0 when it no longer exists.</returns>
    public Task<int> LockScheduleForEvaluationAsync(
        Guid scheduleId,
        CancellationToken cancellationToken = default) =>
        ProjectSchedules
            .Where(schedule => schedule.Id == scheduleId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(schedule => schedule.UpdatedAt, schedule => schedule.UpdatedAt),
                cancellationToken);

    /// <summary>
    /// I-005: writes one probe's outcome onto its project - the three "last probe" columns only.
    /// <para>
    /// <b>Why a guarded statement instead of loading and saving the row.</b> A probe is not a project
    /// edit: loading the row and calling <see cref="SaveChangesAsync(bool, CancellationToken)"/> would let
    /// the timestamp stamping move <c>Projects.UpdatedAt</c>, so a poller probing every minute would make
    /// the overview claim the project was edited every minute. This statement sets exactly the probe
    /// columns (and re-writes <c>UpdatedAt</c> to itself, the same deliberate no-op the lock methods use),
    /// so the project's edit history stays the operator's. The WHERE is the tenant query filter, so a
    /// foreign project id updates nothing.
    /// </para>
    /// <para>
    /// A guarded <c>ExecuteUpdate</c> on a tenant-owned entity outside a system scope - the SET is fixed,
    /// never caller-supplied (see the class doc's bypass discipline).
    /// </para>
    /// </summary>
    /// <returns>1 when the project row was found and updated, 0 when it no longer exists.</returns>
    public Task<int> TryRecordProjectProbeOutcomeAsync(
        Guid projectId,
        string outcome,
        DateTimeOffset probedAt,
        string? message,
        CancellationToken cancellationToken = default) =>
        Projects
            .Where(project => project.Id == projectId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(project => project.LastProbeOutcome, outcome)
                .SetProperty(project => project.LastProbeAt, (DateTimeOffset?)probedAt)
                .SetProperty(project => project.LastProbeMessage, message)
                .SetProperty(project => project.UpdatedAt, project => project.UpdatedAt),
                cancellationToken);

    /// <summary>
    /// F-028: cancels every still-<c>Queued</c> run of one project in a single guarded statement (the
    /// disable's "a disabled job never builds something it queued while enabled"). The same terminal
    /// shape <see cref="TryCancelQueuedBuildRunAsync"/> writes per run - the reason becomes the
    /// <c>FailureReason</c>, the lease columns are cleared - but for the project's whole queue at once.
    /// <para>
    /// The <c>Status == Queued</c> predicate is what leaves a <c>Running</c> run alone: disabling is not
    /// a cancellation of work in flight (that is a second, separate decision), and a claim that leased a
    /// run inside the same window still ends terminal here - its leaseholder's accept is then the
    /// documented 409 (F-009's race-window semantic).
    /// </para>
    /// </summary>
    /// <returns>How many queued runs this call canceled.</returns>
    public Task<int> TryCancelQueuedProjectRunsAsync(
        Guid projectId,
        string failureReason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        BuildRuns
            .Where(run => run.ProjectId == projectId
                && run.Status == BuildRunStatus.Queued)
            .ExecuteUpdateAsync(update => update
                .SetProperty(run => run.Status, BuildRunStatus.Canceled)
                .SetProperty(run => run.EndedAt, (DateTimeOffset?)now)
                .SetProperty(run => run.FailureReason, failureReason)
                .SetProperty(run => run.WorkerId, (Guid?)null)
                .SetProperty(run => run.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(run => run.UpdatedAt, now),
                cancellationToken);

    /// <summary>
    /// F-006: atomic compare-and-set lease of one queued run (the claim endpoint). This is one of
    /// the guarded <c>ExecuteUpdate</c> calls on a tenant-owned entity outside a system scope
    /// (the others are <see cref="LockWorkerForClaimAsync"/>, <see cref="LockProjectForTriggerAsync"/>,
    /// <see cref="TryCancelQueuedProjectRunsAsync"/> and the cancel requests below; see the class
    /// doc's bypass discipline): the SET is fixed here - never caller-supplied - and the WHERE is
    /// the tenant query filter (which applies to <c>ExecuteUpdate</c>) plus the compare-and-set
    /// conditions that make a concurrent double-claim impossible: the run must still be <c>Queued</c>
    /// and still unleased.
    /// <para>
    /// One UPDATE statement is atomic under both providers' write locking: PostgreSQL re-evaluates
    /// the predicate after granting the row lock (READ COMMITTED), which is the equivalent of the
    /// SELECT … FOR UPDATE SKIP LOCKED dance without native SQL, and SQLite serializes database
    /// writes. The loser of a race therefore updates 0 rows and must move on to the next candidate.
    /// Only unleased runs are claimable - the lease sweeper is the single authority on expiry
    /// (SQLite cannot compare <c>DateTimeOffset</c> values in SQL at all, so expiry is a sweep
    /// concern, not a claim predicate).
    /// </para>
    /// </summary>
    /// <returns>1 when this call won the lease, 0 when the run was leased or requeued concurrently.</returns>
    public Task<int> TryLeaseBuildRunAsync(
        Guid runId,
        Guid workerId,
        DateTimeOffset leaseExpiresAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        BuildRuns
            .Where(run => run.Id == runId
                && run.Status == BuildRunStatus.Queued
                && run.LeaseExpiresAt == null)
            .ExecuteUpdateAsync(update => update
                .SetProperty(run => run.WorkerId, workerId)
                .SetProperty(run => run.LeaseExpiresAt, (DateTimeOffset?)leaseExpiresAt)
                .SetProperty(run => run.UpdatedAt, now),
                cancellationToken);

    /// <summary>
    /// F-006: the lease sweeper's requeue of one expired, claimed-but-never-accepted run. The
    /// WHERE restates the exact observed state - still <c>Queued</c>, still carrying precisely the
    /// expired lease that was read - so the statement is a compare-and-set: it can never touch a
    /// run that was accepted (no lease anymore), never touch a valid lease, and never clobber a
    /// lease some other writer stamped meanwhile. Cross-tenant by design (the sweeper serves every
    /// organization), so it requires an active <see cref="ISystemDataScope"/> - same discipline as
    /// <see cref="SystemQueryAsync{TEntity}"/>.
    /// </summary>
    /// <returns>1 when this call requeued the run, 0 when it was already requeued or accepted.</returns>
    public Task<int> RequeueExpiredBuildRunLeaseAsync(
        Guid runId,
        DateTimeOffset expiredLease,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!_systemDataScope.IsActive)
        {
            throw new InvalidOperationException(
                $"Requeueing expired leases spans tenants and requires an active {nameof(ISystemDataScope)} scope.");
        }

        return BuildRuns
            .IgnoreQueryFilters()
            .Where(run => run.Id == runId
                && run.Status == BuildRunStatus.Queued
                && run.LeaseExpiresAt == expiredLease)
            .ExecuteUpdateAsync(update => update
                .SetProperty(run => run.WorkerId, (Guid?)null)
                .SetProperty(run => run.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(run => run.UpdatedAt, now),
                cancellationToken);
    }

    /// <summary>
    /// F-009: the user-facing cancel of one queued run, as a guarded compare-and-set (the claim's
    /// CAS counterpart). The WHERE restates the eligibility - the run must still be
    /// <c>Queued</c> - so under a concurrent claim storm exactly one of the two statements can win
    /// the row: a claim that leases first leaves the run <c>Queued</c> (leased) and this cancel
    /// then still cancels it (the documented race-window semantic: the leaseholder's accept is a
    /// 409), while a cancel that lands first makes every claim's WHERE (<c>Status == Queued</c>)
    /// re-evaluate to false. Tenant-scoped like <see cref="TryLeaseBuildRunAsync"/>: the user
    /// path runs behind the active organization's filter. The lease columns are cleared with the
    /// terminal state - a canceled run never carries a stale lease.
    /// </summary>
    /// <returns>1 when this call canceled the run, 0 when it was no longer queued (leased onward,
    /// already canceled, or terminal).</returns>
    public Task<int> TryCancelQueuedBuildRunAsync(
        Guid runId,
        string failureReason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        BuildRuns
            .Where(run => run.Id == runId
                && run.Status == BuildRunStatus.Queued)
            .ExecuteUpdateAsync(update => update
                .SetProperty(run => run.Status, BuildRunStatus.Canceled)
                .SetProperty(run => run.EndedAt, (DateTimeOffset?)now)
                .SetProperty(run => run.FailureReason, failureReason)
                .SetProperty(run => run.WorkerId, (Guid?)null)
                .SetProperty(run => run.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(run => run.UpdatedAt, now),
                cancellationToken);

    /// <summary>
    /// F-009: marks a <c>Running</c> run cancel-requested (the Queued case never gets here - a
    /// queued cancel is terminal via <see cref="TryCancelQueuedBuildRunAsync"/>). The run keeps
    /// <c>Running</c> until its worker kills the process tree and reports through the canceled
    /// endpoint; the recorded reason becomes the terminal <c>FailureReason</c>. The
    /// <c>CancelRequestedAt IS NULL</c> condition makes the first request win: a double cancel or
    /// a user cancel racing the deadline sweeper records one reason, never overwrites one.
    /// Tenant-scoped (the user path); the deadline sweeper uses the cross-tenant variant below.
    /// </summary>
    /// <returns>1 when this call recorded the request, 0 when one was already pending or the run
    /// left <c>Running</c> meanwhile.</returns>
    public Task<int> TryRequestBuildRunCancelAsync(
        Guid runId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        BuildRuns
            .Where(run => run.Id == runId
                && run.Status == BuildRunStatus.Running
                && run.CancelRequestedAt == null)
            .ExecuteUpdateAsync(update => update
                .SetProperty(run => run.CancelRequestedAt, (DateTimeOffset?)now)
                .SetProperty(run => run.CancelReason, reason)
                .SetProperty(run => run.UpdatedAt, now),
                cancellationToken);

    /// <summary>
    /// F-009: the deadline sweeper's variant of <see cref="TryRequestBuildRunCancelAsync"/> for
    /// every organization at once. Cross-tenant by design, so it requires an active
    /// <see cref="ISystemDataScope"/> and bypasses the query filters - same discipline as
    /// <see cref="RequeueExpiredBuildRunLeaseAsync"/>.
    /// </summary>
    public Task<int> TryRequestBuildRunCancelAcrossTenantsAsync(
        Guid runId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!_systemDataScope.IsActive)
        {
            throw new InvalidOperationException(
                $"Requesting run cancellations across tenants requires an active {nameof(ISystemDataScope)} scope.");
        }

        return BuildRuns
            .IgnoreQueryFilters()
            .Where(run => run.Id == runId
                && run.Status == BuildRunStatus.Running
                && run.CancelRequestedAt == null)
            .ExecuteUpdateAsync(update => update
                .SetProperty(run => run.CancelRequestedAt, (DateTimeOffset?)now)
                .SetProperty(run => run.CancelReason, reason)
                .SetProperty(run => run.UpdatedAt, now),
                cancellationToken);
    }

    /// <summary>
    /// F-009/F-010: the server-side terminal transition of one <c>Running</c> run (the grace
    /// period's <c>Lost</c> and the offline monitor's <c>Lost</c>). The WHERE is the compare-and-set:
    /// exactly one sweep wins the Running state, so a late concurrent tick can never duplicate or
    /// overwrite a recorded transition (a re-terminated run would move <c>EndedAt</c>). Cross-tenant
    /// by design (both callers are background sweeps), so it requires an active
    /// <see cref="ISystemDataScope"/>.
    /// </summary>
    /// <returns>1 when this call terminated the run, 0 when it had already left <c>Running</c>.</returns>
    public Task<int> TryTerminateRunningBuildRunAsync(
        Guid runId,
        string targetStatus,
        string failureReason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!_systemDataScope.IsActive)
        {
            throw new InvalidOperationException(
                $"Terminating runs across tenants requires an active {nameof(ISystemDataScope)} scope.");
        }

        return BuildRuns
            .IgnoreQueryFilters()
            .Where(run => run.Id == runId
                && run.Status == BuildRunStatus.Running)
            .ExecuteUpdateAsync(update => update
                .SetProperty(run => run.Status, targetStatus)
                .SetProperty(run => run.FailureReason, failureReason)
                .SetProperty(run => run.EndedAt, (DateTimeOffset?)now)
                .SetProperty(run => run.UpdatedAt, now),
                cancellationToken);
    }

    /// <summary>
    /// F-009: the executing worker's canceled report, as a guarded compare-and-set. The WHERE
    /// restates the caller's authority - the run must still be <c>Running</c> AND assigned to this
    /// worker - so a report racing the grace-period/offline Lost transitions updates zero rows and
    /// the endpoint answers the honest 409 instead of overwriting a recorded terminal state. The
    /// target (<c>Canceled</c> or <c>Failed</c> for the timeout outcome) and the reason are the
    /// server's, never caller-supplied free text. Tenant-scoped: the worker token selected the
    /// tenant before this runs.
    /// </summary>
    /// <returns>1 when this call recorded the terminal state, 0 when the run had already left
    /// <c>Running</c>.</returns>
    public Task<int> TryReportCanceledBuildRunAsync(
        Guid runId,
        Guid workerId,
        string targetStatus,
        string failureReason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        BuildRuns
            .Where(run => run.Id == runId
                && run.WorkerId == workerId
                && run.Status == BuildRunStatus.Running)
            .ExecuteUpdateAsync(update => update
                .SetProperty(run => run.Status, targetStatus)
                .SetProperty(run => run.FailureReason, failureReason)
                .SetProperty(run => run.EndedAt, (DateTimeOffset?)now)
                .SetProperty(run => run.UpdatedAt, now),
                cancellationToken);

    /// <summary>
    /// F-013: records the commit a worker reported with its <c>accepted</c> report, as a guarded
    /// compare-and-set. The WHERE is <c>GitCommit IS NULL</c>, so the column is written exactly once:
    /// a re-delivered accept, a second report, or a future caller cannot rewrite what the run built.
    /// The SET is fixed here (never caller-supplied text) and the WHERE goes through the tenant query
    /// filter, which is the same discipline <see cref="TryLeaseBuildRunAsync"/> follows.
    /// </summary>
    /// <returns>1 when this call recorded the commit, 0 when the column was already set.</returns>
    public Task<int> TryStampGitCommitAsync(
        Guid runId,
        string gitCommit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gitCommit);

        return BuildRuns
            .Where(run => run.Id == runId && run.GitCommit == null)
            .ExecuteUpdateAsync(update => update
                .SetProperty(run => run.GitCommit, gitCommit)
                .SetProperty(run => run.UpdatedAt, now),
                cancellationToken);
    }

    /// <summary>
    /// F-014: the single <c>Queued → Running</c> transition as a guarded compare-and-set. The WHERE
    /// restates what a legal accept needs - still <c>Queued</c>, still leased to this worker, still
    /// carrying a lease - so two concurrent accepted reports for the same run can never both win: the
    /// loser updates zero rows and answers a conflict (before this card the write was an unguarded
    /// entity update, which made the check-then-act window between two accepts a real race).
    /// <para>
    /// The lease's expiry instant is deliberately <b>not</b> in the WHERE: SQLite cannot compare
    /// <c>DateTimeOffset</c> values in SQL (the same constraint the lease sweeper documents), so the
    /// caller checks it in memory against the row it read. The predicate still keeps that from being
    /// the whole story: whichever accept arrives second sees <c>Status != Queued</c> and stops.
    /// </para>
    /// </summary>
    /// <returns>1 when this call won the transition, 0 when another accept (or a cancel) got there first.</returns>
    public Task<int> TryAcceptBuildRunAsync(
        Guid runId,
        Guid workerId,
        DateTimeOffset startedAt,
        DateTimeOffset deadlineAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        BuildRuns
            .Where(run => run.Id == runId
                && run.WorkerId == workerId
                && run.Status == BuildRunStatus.Queued
                && run.LeaseExpiresAt != null)
            .ExecuteUpdateAsync(update => update
                .SetProperty(run => run.Status, BuildRunStatus.Running)
                .SetProperty(run => run.StartedAt, (DateTimeOffset?)startedAt)
                .SetProperty(run => run.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(run => run.DeadlineAt, (DateTimeOffset?)deadlineAt)
                .SetProperty(run => run.UpdatedAt, now),
                cancellationToken);

    /// <summary>
    /// F-014: freezes the validated pipeline spec on a running run - the definition report's write.
    /// The WHERE is <c>PipelineSpecJson IS NULL</c>, so the spec is written exactly once: a re-delivered
    /// report, or a second one, cannot rewrite what the run executed. The WHERE also restates the
    /// caller's authority (still <c>Running</c>, still assigned to this worker), so a report racing a
    /// Lost transition updates nothing.
    /// </summary>
    /// <returns>1 when this call froze the spec, 0 when the run already had one or had left Running.</returns>
    public Task<int> FreezePipelineDefinitionAsync(
        Guid runId,
        Guid workerId,
        string pipelineSpecJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineSpecJson);

        return BuildRuns
            .Where(run => run.Id == runId
                && run.WorkerId == workerId
                && run.Status == BuildRunStatus.Running
                && run.PipelineSpecJson == null)
            .ExecuteUpdateAsync(update => update
                .SetProperty(run => run.PipelineSpecJson, pipelineSpecJson)
                .SetProperty(run => run.UpdatedAt, now),
                cancellationToken);
    }

    /// <summary>
    /// F-010: the offline monitor's Online → Offline flip of one stale worker, as a guarded
    /// compare-and-set. Only the sweep that wins this statement may mark the worker's runs Lost,
    /// which is what makes the Lost transition exactly once even when monitor ticks race.
    /// <para>
    /// <paramref name="observedLastHeartbeatAt"/> is the staleness evidence: the heartbeat value
    /// the sweep's snapshot read. The WHERE demands it still be the row's current heartbeat, so a
    /// fresh heartbeat that lands between the snapshot and this statement leaves zero rows - the
    /// sweep skips that worker's runs and the next tick re-evaluates it from a fresh snapshot.
    /// Without the guard, a beat landing in that window would still be flipped Offline and its
    /// Running runs wrongly Lost. Cross-tenant by design, so it requires an active
    /// <see cref="ISystemDataScope"/>.
    /// </para>
    /// </summary>
    /// <returns>1 when this call flipped the worker, 0 when it was already Offline or a fresh
    /// heartbeat had replaced the observed one.</returns>
    public Task<int> TryMarkWorkerOfflineAsync(
        Guid workerId,
        DateTimeOffset observedLastHeartbeatAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!_systemDataScope.IsActive)
        {
            throw new InvalidOperationException(
                $"Marking workers Offline across tenants requires an active {nameof(ISystemDataScope)} scope.");
        }

        return Workers
            .IgnoreQueryFilters()
            .Where(worker => worker.Id == workerId
                && worker.Status == WorkerStatus.Online
                && worker.LastHeartbeatAt == observedLastHeartbeatAt)
            .ExecuteUpdateAsync(update => update
                .SetProperty(worker => worker.Status, WorkerStatus.Offline)
                .SetProperty(worker => worker.UpdatedAt, now),
                cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Identity configuration first (F-001 re-bases this model on IdentityUserContext); a missing
        // base call would compile but silently drop the Identity entity configuration.
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.Property(user => user.CreatedAt).IsRequired();

            // F-002's server-side organization selection. The FK is only an integrity net: the
            // resolver still re-validates the selection against OrganizationMember on every request,
            // because "member of that organization" cannot be expressed as a foreign key. SetNull
            // keeps an account alive if an organization is ever deleted.
            entity.HasOne<Organization>()
                .WithMany()
                .HasForeignKey(user => user.ActiveOrganizationId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<BootstrapMarker>(entity =>
        {
            // The fixed primary key is the race guard: the database, not a count check, decides the
            // winner of two concurrent first registrations.
            entity.HasKey(marker => marker.Id);
            entity.Property(marker => marker.Id).HasMaxLength(32);
            entity.Property(marker => marker.UserId).IsRequired().HasMaxLength(450);
            entity.Property(marker => marker.CompletedAt).IsRequired();
        });

        modelBuilder.Entity<Organization>(entity =>
        {
            entity.HasKey(organization => organization.Id);
            entity.Property(organization => organization.Name).IsRequired().HasMaxLength(200);
        });

        modelBuilder.Entity<OrganizationMember>(entity =>
        {
            entity.HasKey(member => member.Id);
            entity.Property(member => member.UserId).IsRequired().HasMaxLength(450);
            entity.Property(member => member.Role).HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(member => new { member.OrganizationId, member.UserId }).IsUnique();
            entity.HasOne<Organization>().WithMany().HasForeignKey(member => member.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);

            // F-001: the membership's user id is now a real foreign key to the Identity users table.
            // Deleting an account removes its memberships; it never touches the organization.
            entity.HasOne(member => member.User).WithMany().HasForeignKey(member => member.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Belt and braces for the string conversion above: a row written by raw SQL with an
            // unknown role value would otherwise make every member listing throw.
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_OrganizationMembers_Role",
                $"\"Role\" IN ('{nameof(OrganizationRole.Owner)}', '{nameof(OrganizationRole.Member)}')"));

            entity.HasQueryFilter(member => member.OrganizationId == CurrentOrganizationId);
        });

        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(project => project.Id);
            entity.Property(project => project.Name).IsRequired().HasMaxLength(200);

            // F-003: a project name is unique inside an organization (PRD §3.2 / card TC-002). The
            // index is the guard, not a pre-flight query, so two concurrent creates cannot both
            // pass a "does it exist" check; ProjectService translates the violation into a
            // field-level error.
            entity.HasIndex(project => new { project.OrganizationId, project.Name }).IsUnique();

            // F-020: the project's kind. The repository fields were required since F-003; a Freestyle
            // project has none of them, so they became nullable - the validation layer (not the data
            // layer) enforces them for Pipeline projects, and a null repository URL never reaches a
            // clone because the Freestyle path skips it.
            entity.Property(project => project.Kind).IsRequired().HasMaxLength(16);

            // F-022: where a Pipeline project's definition lives. The column is immutable after create
            // (enforced by ProjectService, the same shape Kind follows) and every existing row is a
            // repository project - the migration's default is what keeps history honest.
            entity.Property(project => project.PipelineSource).IsRequired().HasMaxLength(16)
                .HasDefaultValue(ProjectSources.Repository);

            // F-022: the inline Lua body. No max length on purpose (the column is text): the 64 KiB
            // bound is enforced when the script is saved (PipelineScriptLimits.MaxScriptBytes), the
            // same discipline the frozen spec follows.
            entity.Property(project => project.InlineScript);
            entity.Property(project => project.RepositoryUrl).HasMaxLength(2048);
            entity.Property(project => project.DefaultBranch).HasMaxLength(200);
            entity.Property(project => project.PipelinePath).HasMaxLength(512);

            // F-020: the Freestyle project's frozen steps. No max length on purpose (the column is
            // text): the size cap is enforced when the spec is saved (PipelineSpecSchema.MaxJsonBytes),
            // the same rule the run's frozen spec follows.
            entity.Property(project => project.FreestyleSpecJson);

            // F-021: the project's parameter definitions, bounded by ProjectParameterLimits when the
            // definitions are saved (the same discipline the caps elsewhere follow).
            entity.Property(project => project.ParametersJson);

            // F-028: the reversible disable switch. The reason is the operator's own words, bounded
            // like every other user-authored text; the attribution columns carry no foreign key (the
            // GitCredential pattern), so an account deletion never rewrites a project's history.
            entity.Property(project => project.DisabledReason).HasMaxLength(ProjectValidation.MaxDisabledReasonLength);
            entity.Property(project => project.DisabledByUserId).HasMaxLength(450);

            // I-005: the probe's last outcome on the project - the word itself (bounded) and the
            // diagnostic the probe carried. The outcome's closed vocabulary is a check constraint below.
            entity.Property(project => project.LastProbeOutcome).HasMaxLength(32);
            entity.Property(project => project.LastProbeMessage).HasMaxLength(200);

            // F-029: the three queue controls. The check constraints restate the validation the service
            // performs, so a row written out of band (raw SQL, a future caller) cannot declare a limit
            // no claim could satisfy: a concurrency cap below 1 or a queue cap below 1 is a configured
            // starvation, and a negative quiet period is meaningless.

            // F-023: the branch-discovery mode. Bounded and closed: a value outside the two modes is not
            // a mode a read path could honour. The migration's default keeps every historical row on
            // `single`, which is why existing projects behave exactly as before the card.
            entity.Property(project => project.BranchDiscovery).IsRequired().HasMaxLength(16)
                .HasDefaultValue(BranchDiscoveryModes.Single);
            entity.HasOne<Organization>().WithMany().HasForeignKey(project => project.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);

            // Belt and braces for the string kind, same shape as the run status check below, plus
            // F-029's bounded queue controls.
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_Projects_Kind",
                    $"\"Kind\" IN ('{ProjectKinds.Pipeline}', '{ProjectKinds.Freestyle}')");

                // F-022: the source is a closed vocabulary too. The default keeps every historical row
                // a repository project; an inline row cannot be invented by an out-of-band writer.
                table.HasCheckConstraint(
                    "CK_Projects_PipelineSource",
                    $"\"PipelineSource\" IN ('{ProjectSources.Repository}', '{ProjectSources.Inline}')");
                table.HasCheckConstraint(
                    "CK_Projects_MaxConcurrentRuns",
                    "\"MaxConcurrentRuns\" IS NULL OR \"MaxConcurrentRuns\" >= 1");
                table.HasCheckConstraint(
                    "CK_Projects_QuietPeriodSeconds",
                    "\"QuietPeriodSeconds\" >= 0");
                table.HasCheckConstraint(
                    "CK_Projects_MaxQueuedRuns",
                    "\"MaxQueuedRuns\" IS NULL OR \"MaxQueuedRuns\" >= 1");

                // F-023: the discovery mode is a closed vocabulary, like the kind and the source beside
                // it. An out-of-band writer cannot invent a third mode no read path understands.
                table.HasCheckConstraint(
                    "CK_Projects_BranchDiscovery",
                    "\"BranchDiscovery\" IN ("
                    + string.Join(", ", BranchDiscoveryModes.All.Select(mode => $"'" + mode + "'"))
                    + ")");

                // I-005: the last probe outcome is one of the shared vocabulary's words (or NULL for a
                // project that was never probed). The constraint keeps an out-of-band writer from
                // inventing a word no caller can interpret - the same discipline the status columns use.
                table.HasCheckConstraint(
                    "CK_Projects_LastProbeOutcome",
                    "\"LastProbeOutcome\" IS NULL OR \"LastProbeOutcome\" IN ("
                    + string.Join(", ", GitProbeOutcomes.All.Select(outcome => $"'" + outcome + "'"))
                    + ")");
            });

            entity.HasQueryFilter(project => project.OrganizationId == CurrentOrganizationId);
        });

        // F-004: workers and their bearer tokens are tenant-owned like projects. Every worker-API
        // call authenticates with a token, and the token - never a cookie or URL - selects the
        // tenant for those requests (WorkerService sets it after resolving the hash).
        modelBuilder.Entity<Worker>(entity =>
        {
            entity.HasKey(worker => worker.Id);
            entity.Property(worker => worker.Name).IsRequired().HasMaxLength(200);

            // Container restarts must not duplicate workers: re-registering with a known name
            // updates the row. The database index is the guard, not a pre-flight query.
            entity.HasIndex(worker => new { worker.OrganizationId, worker.Name }).IsUnique();
            entity.Property(worker => worker.Os).IsRequired().HasMaxLength(32);
            entity.Property(worker => worker.Architecture).IsRequired().HasMaxLength(32);
            entity.Property(worker => worker.Version).IsRequired().HasMaxLength(64);
            entity.Property(worker => worker.Tags).IsRequired().HasMaxLength(400);
            entity.Property(worker => worker.Status).IsRequired().HasMaxLength(16);
            entity.HasOne<Organization>().WithMany().HasForeignKey(worker => worker.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);

            // Belt and braces for the string status, same shape as the role check above.
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_Workers_Status",
                $"\"Status\" IN ('{WorkerStatus.Online}', '{WorkerStatus.Offline}')"));

            entity.HasQueryFilter(worker => worker.OrganizationId == CurrentOrganizationId);
        });

        modelBuilder.Entity<WorkerToken>(entity =>
        {
            entity.HasKey(token => token.Id);
            entity.Property(token => token.Kind).IsRequired().HasMaxLength(16);

            // M1 (review fix): the one-time transitions of a token are guarded by EF optimistic
            // concurrency. Two concurrent registrations racing to burn the same single-use token
            // (UsedAt) - or a revoke racing a register/heartbeat write (RevokedAt) - cannot both
            // commit: the loser's UPDATE matches no row and surfaces as
            // DbUpdateConcurrencyException, which WorkerService maps to 401. Plain concurrency
            // tokens change no column and no index, so no migration is needed for them.

            // The hash is the authentication lookup key, so it is globally unique (also the cheap
            // index the system-scope lookup uses; plaintext tokens are never stored or logged).
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.Property(token => token.TokenHash).IsRequired().HasMaxLength(64);
            entity.HasOne<Organization>().WithMany().HasForeignKey(token => token.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Worker>().WithMany().HasForeignKey(token => token.WorkerId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.ToTable(table => table.HasCheckConstraint(
                "CK_WorkerTokens_Kind",
                $"\"Kind\" IN ('{WorkerTokenKind.Registration}', '{WorkerTokenKind.Worker}')"));

            entity.HasQueryFilter(token => token.OrganizationId == CurrentOrganizationId);

            entity.Property(token => token.UsedAt).IsConcurrencyToken();
            entity.Property(token => token.RevokedAt).IsConcurrencyToken();
        });

        // F-006: build runs are tenant-owned like workers and projects. A claim does not change the
        // status - it stamps WorkerId + LeaseExpiresAt atomically via TryLeaseBuildRunAsync (the one
        // guarded ExecuteUpdate on a tenant-owned entity outside a system scope); the lease sweeper
        // requeues expired never-accepted leases via RequeueExpiredBuildRunLeaseAsync (system
        // scope). State transitions go through the transition service only.
        modelBuilder.Entity<BuildRun>(entity =>
        {
            entity.HasKey(run => run.Id);
            entity.Property(run => run.Status).IsRequired().HasMaxLength(16);
            entity.Property(run => run.TriggerType).IsRequired().HasMaxLength(16);
            entity.Property(run => run.TriggerUserId).HasMaxLength(450);
            entity.Property(run => run.RequiredTags).IsRequired().HasMaxLength(400);

            // F-014: every run is a pipeline run. The pipeline path and branch are snapshots taken at
            // trigger time (a later project edit must not move an existing run's target), bounded like
            // the project fields they were copied from.
            entity.Property(run => run.PipelinePath).IsRequired()
                .HasMaxLength(ProjectValidation.MaxPipelinePathLength);
            entity.Property(run => run.GitBranch).IsRequired()
                .HasMaxLength(ProjectValidation.MaxDefaultBranchLength);

            // F-014: the frozen spec. No max length on purpose (the column is text): the size cap is
            // enforced when the definition is accepted (PipelineSpecSchema.MaxJsonBytes), where an
            // over-long spec is a clear 400 rather than a storage error.
            entity.Property(run => run.PipelineSpecJson);

            // F-022: the inline run's script snapshot. Text on purpose: the 64 KiB bound is enforced
            // where the text is authored (project save); the trigger only requires the script to exist,
            // and the Worker re-checks the same bound before compiling it.
            entity.Property(run => run.PipelineScriptText);

            // F-021: the run's resolved parameter values (a name → value object), bounded by
            // ProjectParameterLimits when both the definitions and the supplied values are validated.
            entity.Property(run => run.ParametersJson);

            // F-034: the frozen organization environment layer (another name → value object). Bounded at
            // the source by OrganizationEnvironmentValidation (100 variables, 4 KiB each), so the column
            // needs no typed cap of its own.
            entity.Property(run => run.EnvironmentJson);
            entity.Property(run => run.FailureReason).HasMaxLength(200);

            // F-009: the reason a cancellation was requested (canceled-by-user <user>, timeout),
            // bounded like FailureReason which it becomes when the worker reports the kill.
            entity.Property(run => run.CancelReason).HasMaxLength(200);

            // The claim query filters by tenant + status and orders by creation (FIFO); the project
            // runs list filters by project. Both are small explicit indexes.
            entity.HasIndex(run => new { run.OrganizationId, run.Status });

            // F-036: the build number is unique per project, and the index - not a pre-flight query -
            // is what makes "two runs never share #N" true under concurrent triggers. It also serves
            // the project's run list and the permalink lookup (`/projects/{id}/runs/{number}`), which
            // are exactly (ProjectId, Number) reads, so the old single-column ProjectId index would be
            // a strict prefix of this one and is replaced by it.
            entity.HasIndex(run => new { run.ProjectId, run.Number }).IsUnique();

            // F-037: the re-run attribution carries no foreign key on purpose (see
            // BuildRun.RebuiltFromRunId): a rebuild outlives the run it came from, and deleting a
            // project cascades its own runs away without touching another row.

            entity.HasOne<Organization>().WithMany().HasForeignKey(run => run.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(run => run.Project).WithMany().HasForeignKey(run => run.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            // Attribution is best-effort: deleting a worker (no product path today) keeps the run
            // rows but forgets which worker executed them. Never blocks an organization cascade.
            entity.HasOne(run => run.Worker).WithMany().HasForeignKey(run => run.WorkerId)
                .OnDelete(DeleteBehavior.SetNull);

            // Belt and braces for the string status, same shape as the worker status check above.
            //
            // F-036: the build number is positive, and the constraint is what keeps the *first* out-of-band
            // insert (or a future caller that forgot to assign one) from inventing `#0`: the column's
            // migration default of 0 is gone, and 0 is refused by the database rather than by a comment.
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_BuildRuns_Status",
                    $"\"Status\" IN ('{BuildRunStatus.Queued}', '{BuildRunStatus.Running}', "
                    + $"'{BuildRunStatus.Success}', '{BuildRunStatus.Failed}', "
                    + $"'{BuildRunStatus.Canceled}', '{BuildRunStatus.Lost}')");
                table.HasCheckConstraint("CK_BuildRuns_Number", "\"Number\" > 0");
            });

            entity.HasQueryFilter(run => run.OrganizationId == CurrentOrganizationId);
        });

        // F-008: log lines are tenant-owned like everything else (direct OrganizationId column, see
        // BuildLog's doc for why the filter is a column and not a BuildRun join). The composite
        // (RunId, Seq) primary key IS the reconnection-deduplication authority: the database, not
        // a pre-flight query, absorbs a reconnecting worker's overlapping re-delivery.
        modelBuilder.Entity<BuildLog>(entity =>
        {
            entity.HasKey(log => new { log.RunId, log.Seq });
            entity.Property(log => log.Stream).IsRequired().HasMaxLength(16);

            // No max length on purpose: a child process can emit arbitrarily long lines.
            entity.Property(log => log.Message).IsRequired();
            entity.Property(log => log.Timestamp).IsRequired();

            entity.HasOne<Organization>().WithMany().HasForeignKey(log => log.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<BuildRun>().WithMany().HasForeignKey(log => log.RunId)
                .OnDelete(DeleteBehavior.Cascade);

            // Belt and braces for the string stream, same shape as the run status check above.
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_BuildLogs_Stream",
                $"\"Stream\" IN ('{LogStream.StdOut}', '{LogStream.StdErr}')"));

            entity.HasQueryFilter(log => log.OrganizationId == CurrentOrganizationId);
        });

        // F-015: one row per step position of a run. The unique (RunId, Index) index is the guard, not a
        // pre-flight query: two concurrent reports of the same step position cannot both insert, so the
        // worker's re-delivery of a report (a lost response) lands on the row that already exists. The
        // rows cascade with their run - a step is not meaningful without it.
        modelBuilder.Entity<BuildStep>(entity =>
        {
            entity.HasKey(step => step.Id);
            entity.Property(step => step.Name).IsRequired()
                .HasMaxLength(PipelineSpecSchema.MaxNameLength);
            entity.Property(step => step.Status).IsRequired().HasMaxLength(16);
            entity.Property(step => step.FailureReason).HasMaxLength(64);

            // F-032: one attempt by default - the value every step row written before this card really
            // had. The database default is 1 so adding the column to a populated table backfills the
            // honest answer instead of a zero the check constraint below would then refuse.
            entity.Property(step => step.Attempts).HasDefaultValue(PipelineSpecSchema.MinStepRetry);

            entity.HasIndex(step => new { step.RunId, step.Index }).IsUnique();

            entity.HasOne<Organization>().WithMany().HasForeignKey(step => step.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(step => step.Run).WithMany().HasForeignKey(step => step.RunId)
                .OnDelete(DeleteBehavior.Cascade);

            // Belt and braces for the string status, same shape as the run status check above. Pending
            // is deliberately absent: it is a view state for a step with no row, never a stored value.
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_BuildSteps_Status",
                $"\"Status\" IN ('{PipelineStepStatus.Running}', '{PipelineStepStatus.Success}', "
                + $"'{PipelineStepStatus.Failed}', '{PipelineStepStatus.Skipped}')"));

            // F-032: the attempt count is bounded by the same cap the schema enforces (1..5), so a row can
            // never claim an attempt loop the executor could not have run. The column exists because one
            // step record survives its attempts - the alternative (a row per attempt) would break "step N
            // of the frozen spec", which artifacts and conditions index by.
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_BuildSteps_Attempts",
                $"\"Attempts\" BETWEEN {PipelineSpecSchema.MinStepRetry} AND {PipelineSpecSchema.MaxStepRetry}"));

            entity.HasQueryFilter(step => step.OrganizationId == CurrentOrganizationId);
        });

        // F-012: secrets are tenant-owned and write-only above the storage layer. The name is the
        // identity: unique per organization (case-sensitive - both providers compare text
        // case-sensitively by default), env-var shaped, and immutable (a rename is delete + create).
        modelBuilder.Entity<Secret>(entity =>
        {
            entity.HasKey(secret => secret.Id);
            entity.Property(secret => secret.Name).IsRequired().HasMaxLength(SecretValidation.MaxNameLength);

            // No max length on purpose: the ciphertext's length follows the plaintext (Data
            // Protection output), and the form limit - not the column - bounds the value.
            entity.Property(secret => secret.Ciphertext).IsRequired();

            entity.Property(secret => secret.CreatedByUserId).IsRequired().HasMaxLength(450);

            // D-006: who last rotated it. Nullable because a secret created before this column (or never
            // rotated) has no answer - the page says "not recorded" rather than inventing a name.
            entity.Property(secret => secret.RotatedByUserId).HasMaxLength(450);

            // The guard, not a pre-flight query: two concurrent creates cannot both pass an
            // "is the name free" check. SecretService translates the violation into a field error.
            entity.HasIndex(secret => new { secret.OrganizationId, secret.Name }).IsUnique();
            entity.HasOne<Organization>().WithMany().HasForeignKey(secret => secret.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasQueryFilter(secret => secret.OrganizationId == CurrentOrganizationId);
        });

        // F-034: the organization's environment variables. Tenant-owned like everything else, name-unique
        // per organization; the value is plaintext on purpose (configuration, not a secret). The reserved
        // prefix is refused in the service at save time, not silently resolved by the worker.
        modelBuilder.Entity<OrganizationEnvironmentVariable>(entity =>
        {
            entity.HasKey(variable => variable.Id);
            entity.Property(variable => variable.Name).IsRequired()
                .HasMaxLength(OrganizationEnvironmentValidation.MaxNameLength);
            entity.Property(variable => variable.Value).IsRequired()
                .HasMaxLength(OrganizationEnvironmentValidation.MaxValueLength);
            entity.Property(variable => variable.CreatedByUserId).IsRequired().HasMaxLength(450);
            entity.Property(variable => variable.UpdatedByUserId).HasMaxLength(450);

            // The guard, not a pre-flight query: two concurrent creates cannot both pass an "is the name
            // free" check. The service translates the violation into a field error.
            entity.HasIndex(variable => new { variable.OrganizationId, variable.Name }).IsUnique();
            entity.HasOne<Organization>().WithMany()
                .HasForeignKey(variable => variable.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasQueryFilter(variable => variable.OrganizationId == CurrentOrganizationId);
        });

        // F-013: the Git credential entity (tenant-owned, name-unique) and BuildRun.GitCommit's bounded
        // column. The configuration lives next to the feature (src/Server/Git) so the model grows by one
        // call here, not by two blocks; the tenant predicate is installed there against this instance.
        modelBuilder.ConfigureGitCredentials(this);

        // F-016: the artifact entity (tenant-owned, unique (RunId, Name), cascade from the run). Same
        // shape as the call above: the storage rules live next to the feature (src/Server/Artifacts).
        modelBuilder.ConfigureArtifacts(this);

        // F-017: the webhook and its replay records (tenant-owned; unique (ProjectId, Name) and
        // (WebhookId, DedupeKey)). The rules live next to the feature (src/Server/Webhooks).
        modelBuilder.ConfigureWebhooks(this);

        // F-027: the organization API tokens and their idempotency records (tenant-owned; globally
        // unique TokenHash, unique (OrganizationId, Name) and (ApiTokenId, Key)). The rules live next
        // to the feature (src/Server/Api).
        modelBuilder.ConfigureApi(this);

        // F-024: the per-project cron schedules and their due-time ledger (tenant-owned; unique
        // (ProjectId, Cron, TimeZone) and (ScheduleId, DueAt)). The rules live next to the feature
        // (src/Server/Schedules).
        modelBuilder.ConfigureSchedules(this);

        // F-023: the branch jobs of a multibranch project (tenant-owned; unique (ProjectId, Branch)).
        // The rules live next to the feature (src/Server/Branches).
        modelBuilder.ConfigureBranches(this);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenantBoundary();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    // The guard is shared with the synchronous overload so there is exactly one isolation code path
    // to review. It performs no I/O for inserts; only updates/deletes do one row lookup each, and
    // v0.1 has no async write path yet (F-001 adds the first one).
    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnforceTenantBoundary();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Rows tracked under the previous tenant must never be reachable through Find/Include, which
    /// bypass the query filter. Sampling <see cref="ITenantContext.TenantRevision"/> here keeps this
    /// working for any <see cref="ITenantContext"/> implementation, not just <see cref="TenantContext"/>.
    /// A switch with pending work is rejected instead of silently discarding it.
    /// </summary>
    private void DropTrackerIfTenantChanged()
    {
        var revision = _tenantContext.TenantRevision;
        if (revision == _observedTenantRevision)
        {
            return;
        }

        // Switching tenants mid-unit-of-work would either leak rows or silently discard work; make it
        // loud. Tracked-but-unmodified entries clear silently because nothing is lost.
        if (ChangeTracker.HasChanges())
        {
            throw new TenantIsolationException(
                "Cannot switch the active organization with unsaved changes. "
                + "Finish the save, or discard the edits with ChangeTracker.Clear() and continue on a new context.");
        }

        ChangeTracker.Clear();
        _observedTenantRevision = revision;
    }

    private void EnforceTenantBoundary()
    {
        DropTrackerIfTenantChanged();

        var activeOrganizationId = _tenantContext.OrganizationId;
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in ChangeTracker.Entries())
        {
            StampTimestamps(entry, now);

            if (entry.Entity is Organization organization)
            {
                EnsureOrganizationWriteStaysInScope(entry, organization, activeOrganizationId);
                continue;
            }

            if (entry.Entity is not ITenantOwned owned)
            {
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    if (_systemDataScope.IsActive)
                    {
                        // A system-scope caller must name the organization itself; stamping from a
                        // request tenant would be meaningless there.
                        if (owned.OrganizationId == Guid.Empty)
                        {
                            throw new TenantIsolationException(
                                $"Cannot insert '{entry.Metadata.ClrType.Name}' without an organization. "
                                + "System-scope writes must set OrganizationId explicitly.");
                        }

                        break;
                    }

                    if (activeOrganizationId is null)
                    {
                        throw new TenantIsolationException(
                            $"Cannot insert '{entry.Metadata.ClrType.Name}' without an active organization.");
                    }

                    if (owned.OrganizationId == Guid.Empty)
                    {
                        // Tenant-owned rows are stamped from the request, never from the caller.
                        owned.OrganizationId = activeOrganizationId.Value;
                    }
                    else if (owned.OrganizationId != activeOrganizationId.Value)
                    {
                        throw new TenantIsolationException(
                            $"Cannot insert '{entry.Metadata.ClrType.Name}' for organization '{owned.OrganizationId}': "
                            + $"the active organization is '{activeOrganizationId.Value}'.");
                    }

                    break;

                case EntityState.Modified:
                case EntityState.Deleted:
                    if (_systemDataScope.IsActive)
                    {
                        break;
                    }

                    if (activeOrganizationId is null)
                    {
                        throw new TenantIsolationException(
                            $"Cannot change '{entry.Metadata.ClrType.Name}' without an active organization.");
                    }

                    EnsureRowBelongsToActiveOrganization(entry, owned, activeOrganizationId.Value);
                    break;
            }
        }
    }

    /// <summary>
    /// The tenant root is outside the query filters (it must be readable before a tenant exists), so
    /// its writes are guarded by identity instead: touching another organization is a cross-tenant
    /// write even though the row itself does not carry an <c>OrganizationId</c>.
    /// </summary>
    private void EnsureOrganizationWriteStaysInScope(
        EntityEntry entry,
        Organization organization,
        Guid? activeOrganizationId)
    {
        if (entry.State is not (EntityState.Modified or EntityState.Deleted))
        {
            return;
        }

        if (_systemDataScope.IsActive)
        {
            return;
        }

        if (activeOrganizationId is null || organization.Id != activeOrganizationId.Value)
        {
            throw new TenantIsolationException(
                $"Cannot change or delete organization '{organization.Id}': the active organization is "
                + $"'{activeOrganizationId?.ToString() ?? "<none>"}'.");
        }
    }

    /// <summary>
    /// Single owner of the audit timestamps. F-003 (project management) reads these values and must
    /// not stamp them again.
    /// </summary>
    private static void StampTimestamps(EntityEntry entry, DateTimeOffset now)
    {
        if (entry.State is not (EntityState.Added or EntityState.Modified))
        {
            return;
        }

        if (entry.State == EntityState.Added && entry.Metadata.FindProperty("CreatedAt") is not null)
        {
            entry.Property("CreatedAt").CurrentValue = now;
        }

        if (entry.Metadata.FindProperty("UpdatedAt") is not null)
        {
            entry.Property("UpdatedAt").CurrentValue = now;
        }
    }

    /// <summary>
    /// Tracker values are not trustworthy on their own: <c>Attach</c> lets a caller supply any
    /// "original" value, so an id-swapping request could rewrite another tenant's row while
    /// claiming it is own. The persisted row is therefore checked as well.
    /// </summary>
    private static void EnsureRowBelongsToActiveOrganization(
        EntityEntry entry,
        ITenantOwned owned,
        Guid activeOrganizationId)
    {
        var originalOrganizationId =
            entry.Property(nameof(ITenantOwned.OrganizationId)).OriginalValue as Guid? ?? Guid.Empty;
        if (originalOrganizationId != activeOrganizationId)
        {
            throw new TenantIsolationException(
                $"Cannot change '{entry.Metadata.ClrType.Name}': its loaded organization '{originalOrganizationId}' "
                + $"is not the active organization '{activeOrganizationId}'.");
        }

        if (owned.OrganizationId != activeOrganizationId)
        {
            throw new TenantIsolationException(
                $"OrganizationId of '{entry.Metadata.ClrType.Name}' cannot be changed from "
                + $"'{activeOrganizationId}' to '{owned.OrganizationId}'.");
        }

        var databaseValues = entry.GetDatabaseValues();
        if (databaseValues is null)
        {
            throw new TenantIsolationException(
                $"Cannot change '{entry.Metadata.ClrType.Name}': the row does not exist in the active organization "
                + $"'{activeOrganizationId}'.");
        }

        var storedOrganizationId = databaseValues[nameof(ITenantOwned.OrganizationId)];
        if (storedOrganizationId is not Guid stored || stored != activeOrganizationId)
        {
            throw new TenantIsolationException(
                $"Cannot change '{entry.Metadata.ClrType.Name}': the stored row belongs to organization "
                + $"'{storedOrganizationId}', not the active organization '{activeOrganizationId}'.");
        }
    }
}
