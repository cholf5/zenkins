using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Runs;

namespace Zenkins.Server.Webhooks;

/// <summary>What a delivery did. The endpoint maps each value to one status code.</summary>
public enum WebhookDeliveryStatus
{
    /// <summary>Accepted: a run was queued, or the delivery was deliberately ignored (202).</summary>
    Accepted = 0,

    /// <summary>This delivery was already seen; the answer is the original outcome (202).</summary>
    Duplicate = 2,

    /// <summary>The body is not a push payload this product can read (400).</summary>
    InvalidPayload = 3,

    /// <summary>Unknown, disabled or another tenant's webhook id - the same bare 404 (no oracle).</summary>
    NotFound = 4,

    /// <summary>The signature does not verify (bare 401, no hint about which scheme was tried).</summary>
    InvalidSignature = 5,

}

/// <summary>The answer of one delivery. <see cref="Outcome"/> is the stored disposition.</summary>
public sealed record WebhookDeliveryResult(
    WebhookDeliveryStatus Status,
    string? Outcome = null,
    Guid? RunId = null,
    string? Branch = null,
    string? Commit = null,
    string? ErrorCode = null)
{
    public static WebhookDeliveryResult NotFound() => new(WebhookDeliveryStatus.NotFound);

    public static WebhookDeliveryResult InvalidSignature() => new(WebhookDeliveryStatus.InvalidSignature);

    public static WebhookDeliveryResult InvalidPayload() =>
        new(WebhookDeliveryStatus.InvalidPayload, ErrorCode: WebhookPayloadErrorCodes.PayloadInvalid);

}

/// <summary>
/// The webhook delivery pipeline (card F-017): resolve, verify, normalise, de-duplicate, queue.
/// <para>
/// <b>Order matters, and it is chosen deliberately.</b> The rate limit is the endpoint's first step and is
/// keyed by the raw URL id, before the body is read and before the lookup (reviews m5 and the earlier
/// ruling): "429" and "404" therefore say nothing about whether a webhook exists, and an oversized-body
/// flood cannot slip past the budget. The lookup runs before the signature check, so an unknown, a
/// disabled and another tenant's id all answer the same bare 404 - produced by one helper, with no body and
/// no <c>Content-Type</c>, so the cases are indistinguishable. The signature check runs before parsing, so a
/// bad signature can never be told apart from a malformed body.
/// </para>
/// <para>
/// <b>The tenant is selected by the webhook row.</b> A delivery carries no cookie and no token: the
/// resolved webhook's organization is set on the scoped <see cref="TenantContext"/> - exactly the
/// discipline the worker API follows after resolving a token - so every later read and write goes
/// through the same tenant filter as a signed-in request.
/// </para>
/// <para>
/// <b>Replay is a database property, and the run is in the same transaction (review m6).</b>
/// <see cref="WebhookDelivery"/>'s unique <c>(WebhookId, DedupeKey)</c> index decides which of N concurrent
/// identical deliveries wins; the losers roll back and answer the winner's outcome. The winning record and
/// the run it describes commit together, so a crash can leave neither - never a record that would make
/// every retry a "duplicate" for a push that never built. A duplicate is a 202, never an error: git hosts
/// retry, and a retry is not an attack.
/// </para>
/// <para>
/// <b>Runs are created by the run service.</b> This class decides <em>whether</em> and <em>for which
/// commit</em>; <see cref="RunService.QueueWebhookRunAsync"/> stages the run through the same construction
/// routine the manual trigger uses (same state machine, same snapshots, same queue), on this class's
/// context so the two writes share one transaction.
/// </para>
/// </summary>
public sealed class WebhookDeliveryService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    TenantContext tenantContext,
    ISystemDataScope systemDataScope,
    WebhookSecretProtector protector,
    WebhookOptions options,
    RunService runs,
    TimeProvider timeProvider,
    ILogger<WebhookDeliveryService> logger)
{
    /// <summary>Delivery ids a git host may send, in the order they are read.</summary>
    public static readonly string[] DeliveryIdHeaders =
    [
        "X-GitHub-Delivery", "X-Gitea-Delivery", "X-Gitlab-Event-UUID",
    ];

    /// <summary>
    /// Handles one delivery. <paramref name="body"/> is the <b>raw</b> request body - the exact bytes that
    /// arrived, read once with a cap, never a re-serialisation of a parsed model.
    /// <para>
    /// <b>Precondition: this webhook's budget was already spent by the caller.</b> The endpoint reserves a
    /// slot <em>before</em> it reads the body (review m5), so an oversized-body flood cannot slip past the
    /// 429. Keeping that decision at the edge is what makes the ordering visible in one place instead of
    /// buried here.
    /// </para>
    /// </summary>
    public async Task<WebhookDeliveryResult> HandleAsync(
        Guid webhookId,
        string? hubSignature,
        string? gitLabToken,
        string? deliveryId,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        // 1. Existence, through a system-scope read because no tenant is selected yet. There is no caller
        //    tenant here at all, so the two answers are "a live webhook with this id" and "nothing" - and
        //    the second one is the same bare 404 for an id that never existed and for a disabled one.
        var webhook = await ResolveAsync(webhookId, cancellationToken);
        if (webhook is null || !webhook.Enabled)
        {
            return WebhookDeliveryResult.NotFound();
        }

        // 2. The webhook row selects the tenant for everything that follows (worker-token discipline).
        tenantContext.OrganizationId = webhook.OrganizationId;

        // 3. Signature over the raw bytes. A wrong or absent signature is a bare 401 - no expected value,
        //    no hint about which scheme was tried.
        var secret = protector.Unprotect(webhook.SecretCiphertext);
        if (!WebhookSignature.IsValid(hubSignature, gitLabToken, body.Span, secret))
        {
            logger.LogWarning(
                "Webhook delivery refused. OrganizationId={OrganizationId} ProjectId={ProjectId} WebhookId={WebhookId} Reason=signature",
                webhook.OrganizationId,
                webhook.ProjectId,
                webhookId);
            return WebhookDeliveryResult.InvalidSignature();
        }

        // 4. Normalise: one parser for GitHub, Gitea and GitLab shapes.
        if (!WebhookPayloadParser.TryParse(body, out var payload, out _) || payload is null)
        {
            return WebhookDeliveryResult.InvalidPayload();
        }

        // 5. Classify the ref before the dedupe record: an ignored delivery is still recorded, so a replay
        //    answers the same "ignored: …" instead of looking like a fresh, unknown delivery.
        var refKind = WebhookPayloadParser.ReadRef(payload.Ref, out var branch);
        string outcome;
        switch (refKind)
        {
            case WebhookRefKind.Invalid:
                return WebhookDeliveryResult.InvalidPayload();
            case WebhookRefKind.NotABranch:
                outcome = WebhookOutcomes.IgnoredNotBranch;
                branch = string.Empty;
                break;
            default:
                outcome = WebhookPayloadParser.IsDeleteEvent(payload.After)
                    ? WebhookOutcomes.IgnoredDelete
                    : WebhookOutcomes.Queued;
                break;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // 6. The branch filter. An empty filter means the project's default branch, resolved now (never a
        //    copy taken when the webhook was created).
        var project = await context.Projects
            .Where(candidate => candidate.Id == webhook.ProjectId)
            .Select(candidate => new { candidate.Id, candidate.Kind, candidate.PipelineSource, candidate.DefaultBranch })
            .SingleOrDefaultAsync(cancellationToken);
        if (project is null)
        {
            return WebhookDeliveryResult.NotFound();
        }

        // F-020/F-022, defence in depth: a project with no repository (Freestyle, or an inline Pipeline)
        // has nothing to push to. Webhook creation refuses both, and neither kind nor source is mutable,
        // so this is unreachable in practice - the delivery is still recorded honestly instead of
        // queueing a run nothing can execute. The same shared predicate as creation, not a second guard.
        if (!Zenkins.Shared.Projects.ProjectSources.HasRepository(project.Kind, project.PipelineSource))
        {
            outcome = WebhookOutcomes.IgnoredNoRepository;
        }

        if (outcome == WebhookOutcomes.Queued
            && !WebhookBranchFilter.Matches(webhook.BranchFilter, branch, project.DefaultBranch ?? string.Empty))
        {
            outcome = WebhookOutcomes.IgnoredBranch;
        }

        // 7. Claim the dedupe key and queue the run as ONE unit of work (review m6). The delivery record and
        //    the run it describes either both exist or neither does: a crash in between used to leave a
        //    record that made every retry answer "duplicate" with no run behind it, i.e. that push was lost
        //    for good.
        return await ClaimAndQueueAsync(
            context,
            webhook,
            DedupeKey(body, deliveryId),
            outcome,
            branch,
            payload.After,
            allowStaleReclaim: true,
            cancellationToken);
    }

    /// <summary>
    /// Deletes delivery records that are past the dedupe TTL (the sweeper's tick).
    /// <para>
    /// Cross-tenant by design - the sweeper serves every organization - so it runs in the system scope; the
    /// age filter is in memory because SQLite cannot compare <c>DateTimeOffset</c> values in SQL (the
    /// lease/artifact sweepers' constraint).
    /// </para>
    /// <para>
    /// <b>Scale bound (review m8).</b> It loads the dedupe table to filter by age, so one tick costs one row
    /// per delivery inside the TTL. That is bounded by construction - at most
    /// <c>RateLimitPerMinute × webhooks × TTL</c> records (60/min × 7 days is ~600k per webhook only for a
    /// webhook pushed to every minute for a week, which the limiter still permits but no real host does),
    /// and the table only ever holds what the TTL window can hold. If a deployment ever outgrows one pass,
    /// the fix is a provider-specific time predicate (Postgres compares <c>timestamptz</c> natively; SQLite
    /// stores it as text), not a larger buffer here.
    /// </para>
    /// </summary>
    /// <returns>How many records were pruned.</returns>
    public async Task<int> PruneExpiredDeliveriesAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        using var scope = systemDataScope.BeginScope();
        var rows = await context.SystemQueryAsync<WebhookDelivery>(_ => true, cancellationToken);
        var expired = rows
            .Where(row => now - row.ReceivedAt >= options.DeliveryIdTtl)
            .ToArray();
        if (expired.Length == 0)
        {
            return 0;
        }

        context.Set<WebhookDelivery>().RemoveRange(expired);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogDebug("Webhook deliveries pruned. Count={Count}", expired.Length);
        return expired.Length;
    }

    /// <summary>
    /// The dedupe key: the git host's delivery id when there is one (long TTL), otherwise the body's
    /// SHA-256 (short window). Both halves are hashed, so no attacker-controlled text is stored, and the
    /// key is a fixed length either way.
    /// </summary>
    public static string DedupeKey(ReadOnlyMemory<byte> body, string? deliveryId)
    {
        const string IdPrefix = "id:";
        const string HashPrefix = "hash:";

        return string.IsNullOrWhiteSpace(deliveryId)
            ? HashPrefix + Convert.ToHexStringLower(SHA256.HashData(body.Span))
            : IdPrefix + Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(deliveryId.Trim())));
    }

    /// <summary>
    /// The first delivery id the request carries, or null. A host that sends none falls back to the body
    /// hash window.
    /// </summary>
    public static string? ReadDeliveryId(IReadOnlyDictionary<string, string?> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        foreach (var name in DeliveryIdHeaders)
        {
            if (headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Claims the dedupe key and stages the run in <b>one transaction</b>, so the replay record and the run
    /// it describes can never disagree.
    /// <para>
    /// <b>Atomicity, in order.</b> The insert of the replay record is the gate: the unique
    /// <c>(WebhookId, DedupeKey)</c> index means exactly one of N concurrent identical deliveries can win
    /// it. The run is then staged on the same context and saved in the same transaction; a rollback (or a
    /// crash) therefore leaves neither the record nor the run, which is what makes the push retryable
    /// instead of silently dropped.
    /// </para>
    /// <para>
    /// <b>The duplicate path rolls back first.</b> A unique violation inside a transaction aborts it on
    /// PostgreSQL, so the loser rolls back and <em>then</em> reads the winner's row (outside a transaction)
    /// to answer its outcome.
    /// </para>
    /// </summary>
    private async Task<WebhookDeliveryResult> ClaimAndQueueAsync(
        ZenkinsDbContext context,
        ResolvedWebhook webhook,
        string dedupeKey,
        string outcome,
        string branch,
        string commit,
        bool allowStaleReclaim,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var row = new WebhookDelivery
        {
            OrganizationId = webhook.OrganizationId,
            WebhookId = webhook.Id,
            DedupeKey = dedupeKey,
            Outcome = outcome,
            ReceivedAt = now,
        };
        context.Set<WebhookDelivery>().Add(row);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "WebhookDeliveries"))
        {
            await transaction.RollbackAsync(cancellationToken);
            context.ChangeTracker.Clear();
            return await ResolveExistingAsync(
                context,
                webhook,
                dedupeKey,
                outcome,
                branch,
                commit,
                allowStaleReclaim,
                cancellationToken);
        }

        Guid? runId = null;
        if (outcome == WebhookOutcomes.Queued)
        {
            // 8. The run, staged on this context: the same transaction commits the replay record and the run.
            var run = await runs.QueueWebhookRunAsync(
                context,
                webhook.ProjectId,
                branch,
                commit,
                RunValidation.ParseTags(webhook.RequiredTags),
                cancellationToken);

            if (run.Status == RunOperationStatus.Refused && run.FieldErrors is { Count: > 0 } refusals)
            {
                // Cards F-028 + F-029: the project's own gate refused the trigger (disabled, or the queue
                // is at its cap). Nothing was inserted, and this is NOT a delivery error: the record stays
                // - with the refusal as its outcome instead of "queued" - and the git host is answered
                // accepted. A 4xx here would make most hosts disable the webhook automatically, which is
                // the opposite of what "enable the project again and it works" promises.
                outcome = refusals[0].Code;
                row.Outcome = outcome;
                logger.LogInformation(
                    "Webhook delivery refused by the project gate. OrganizationId={OrganizationId} ProjectId={ProjectId} WebhookId={WebhookId} Outcome={Outcome}",
                    webhook.OrganizationId,
                    webhook.ProjectId,
                    webhook.Id,
                    outcome);
            }
            else if (run.Status != RunOperationStatus.Success || run.RunId is null)
            {
                // Nothing was queued, so nothing is recorded: a retry can build.
                await transaction.RollbackAsync(cancellationToken);
                context.ChangeTracker.Clear();
                return run.Status == RunOperationStatus.NotFound
                    ? WebhookDeliveryResult.NotFound()
                    : WebhookDeliveryResult.InvalidPayload();
            }
            else
            {
                runId = run.RunId;
                row.RunId = runId;
            }
        }

        // 9. The "last delivery" facts on the webhook (the settings page's only delivery history), in the
        //    same unit of work as the record and the run.
        var trackedWebhook = await context.Set<Webhook>()
            .SingleOrDefaultAsync(candidate => candidate.Id == webhook.Id, cancellationToken);
        if (trackedWebhook is not null)
        {
            trackedWebhook.LastDeliveryAt = timeProvider.GetUtcNow();
            trackedWebhook.LastDeliveryOutcome = outcome;
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Webhook delivery accepted. OrganizationId={OrganizationId} ProjectId={ProjectId} WebhookId={WebhookId} Outcome={Outcome} Branch={Branch} Commit={Commit} RunId={RunId}",
            webhook.OrganizationId,
            webhook.ProjectId,
            webhook.Id,
            outcome,
            branch,
            commit,
            runId);

        // The payload's clone_url is deliberately not logged either: it is the sender's claim, and the run
        // never used it (the project's repository is what the worker clones).
        return new WebhookDeliveryResult(
            WebhookDeliveryStatus.Accepted,
            outcome,
            runId,
            branch.Length == 0 ? null : branch,
            commit);
    }

    /// <summary>
    /// A unique violation means this key already has an owner: within its window that owner's outcome is the
    /// answer; past the window it is stale and may be reclaimed (once) by this delivery.
    /// </summary>
    private async Task<WebhookDeliveryResult> ResolveExistingAsync(
        ZenkinsDbContext context,
        ResolvedWebhook webhook,
        string dedupeKey,
        string outcome,
        string branch,
        string commit,
        bool allowStaleReclaim,
        CancellationToken cancellationToken)
    {
        var stored = await context.Set<WebhookDelivery>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.WebhookId == webhook.Id && candidate.DedupeKey == dedupeKey,
                cancellationToken);

        // The winner's row vanished between the insert and this read (its webhook was deleted, or the
        // sweeper pruned it). Nobody holds the key now, so one more claim attempt is safe; a second
        // disappearance answers a duplicate instead of looping.
        if (stored is null)
        {
            return allowStaleReclaim
                ? await ClaimAndQueueAsync(
                    context, webhook, dedupeKey, outcome, branch, commit, allowStaleReclaim: false,
                    cancellationToken)
                : new WebhookDeliveryResult(WebhookDeliveryStatus.Duplicate, outcome);
        }

        // The freshness sample is taken NOW, after the lost insert, not before it. The winner's ReceivedAt
        // was stamped when *its* request started, which can be earlier than this request's own clock
        // sample; comparing against that earlier sample once made a just-inserted row look stale, which is
        // exactly how ten concurrent deliveries could produce two runs.
        if (IsFresh(dedupeKey, stored.ReceivedAt, timeProvider.GetUtcNow()) || !allowStaleReclaim)
        {
            return new WebhookDeliveryResult(
                WebhookDeliveryStatus.Duplicate, stored.Outcome, stored.RunId);
        }

        // Stale: the window that owned this key has ended, so a new delivery may claim it. The delete is
        // the compare-and-set - a concurrent claimer's delete affects zero rows and EF reports it, and that
        // loser answers the duplicate instead of creating a second run.
        context.Set<WebhookDelivery>().Remove(stored);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            context.ChangeTracker.Clear();
            return new WebhookDeliveryResult(
                WebhookDeliveryStatus.Duplicate, stored.Outcome, stored.RunId);
        }

        context.ChangeTracker.Clear();
        return await ClaimAndQueueAsync(
            context, webhook, dedupeKey, outcome, branch, commit, allowStaleReclaim: false, cancellationToken);
    }

    /// <summary>
    /// Whether a stored key is inside its window (long for a delivery id, short for a body hash).
    /// <para>
    /// <b>Deterministic on purpose.</b> One comparison, no "age >= 0" guard: a row stamped a moment in the
    /// future (another connection wrote it microseconds ago, while this request's sample is older) is the
    /// freshest thing there is, and treating it as stale would delete a live replay record and let a second
    /// run through. <c>WebhookReplayTests.A_row_stamped_a_moment_in_the_future_is_still_fresh</c> pins this
    /// without a race.
    /// </para>
    /// </summary>
    private bool IsFresh(string dedupeKey, DateTimeOffset receivedAt, DateTimeOffset now)
    {
        var window = dedupeKey.StartsWith("id:", StringComparison.Ordinal)
            ? options.DeliveryIdTtl
            : options.BodyHashWindow;

        return now - receivedAt < window;
    }

    /// <summary>
    /// Reads one webhook across tenants. The system scope is required and explicit: a delivery arrives
    /// with no tenant selected, and the webhook row is what selects it.
    /// </summary>
    private async Task<ResolvedWebhook?> ResolveAsync(Guid webhookId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        using var scope = systemDataScope.BeginScope();
        var rows = await context.SystemQueryAsync<Webhook>(
            webhook => webhook.Id == webhookId,
            cancellationToken);
        var row = rows.SingleOrDefault();
        return row is null
            ? null
            : new ResolvedWebhook(
                row.Id,
                row.OrganizationId,
                row.ProjectId,
                row.SecretCiphertext,
                row.BranchFilter,
                row.RequiredTags,
                row.Enabled);
    }

    /// <summary>The delivery's view of a webhook: no navigation properties, no tracking.</summary>
    private sealed record ResolvedWebhook(
        Guid Id,
        Guid OrganizationId,
        Guid ProjectId,
        string SecretCiphertext,
        string BranchFilter,
        string RequiredTags,
        bool Enabled);
}
