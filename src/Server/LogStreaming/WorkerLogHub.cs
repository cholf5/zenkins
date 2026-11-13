using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Workers;
using Zenkins.Shared.LogStreaming;

namespace Zenkins.Server.LogStreaming;

/// <summary>Group names for the worker upstream hub. One group per worker id, joined on connect,
/// is how the server addresses a worker's cancel signals (F-009) without keeping connection
/// registries: a reconnecting worker re-joins through its connect handler automatically.</summary>
public static class WorkerLogGroups
{
    public static string ForWorker(Guid workerId) => $"worker:{workerId.ToString("N")}";
}

/// <summary>
/// The worker upstream run-log hub (card F-008) and the worker's cancel-signal receive point
/// (card F-009): the executing worker pushes captured stdout/stderr lines here as they are
/// produced, and receives <c>cancelRun(runId, reason)</c> on the same connection - the outbound
/// direction the PRD §3.4.5 cancel requirement rides, with the heartbeat response as the fallback
/// channel. Authentication is the worker token (the same credential the
/// F-004/F-006 HTTP API accepts), enforced at negotiate time by the
/// <see cref="WorkerTokenAuthenticationDefaults.AuthenticationScheme"/> - an unauthenticated or
/// invalid connection is a bare 401 before any hub method can run.
/// <para>
/// <b>Per-method re-validation:</b> connections live as long as the worker, so every
/// <see cref="AppendLinesAsync"/> re-authenticates the token (revocation recheck, the same
/// discipline as every other worker API call) and re-reads the target run through the
/// token-selected tenant: a run of another organization is indistinguishable from a missing one,
/// and a run assigned to a different worker of the same organization is refused as well - only
/// the executing worker streams a run's log. Every definitive refusal is a <c>HubException</c>
/// carrying the <see cref="LogStreamRejection.Prefix"/> (review fix F1) so the worker's drain
/// can tell it apart from a retryable failure; the dead-token case does not abort the connection
/// (review fix F2) - the prefixed answer is what ends the drain.
/// </para>
/// </summary>
[Authorize(AuthenticationSchemes = WorkerTokenAuthenticationDefaults.AuthenticationScheme)]
public sealed class WorkerLogHub(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    WorkerService workers,
    RunLogIngest ingest) : Hub
{
    /// <summary>The plaintext token is kept per connection for the per-method re-validation.</summary>
    private const string TokenItemKey = "zenkins.workerToken";

    public override async Task OnConnectedAsync()
    {
        var token = WorkerTokenAuthenticationHandler.ReadToken(Context.GetHttpContext());
        if (!string.IsNullOrEmpty(token))
        {
            Context.Items[TokenItemKey] = token;
        }

        // F-009: join this worker's per-worker group so the server can address it with the
        // cancelRun signal. Groups are per connection, so every (re)connect re-joins here - the
        // SignalR fallback channel survives reconnects without server-side connection registries.
        if (Guid.TryParse(Context.User?.FindFirstValue(ZenkinsClaims.WorkerId), out var workerId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, WorkerLogGroups.ForWorker(workerId));
        }

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Receives one batch of lines for one run. The worker sends flush-sized batches in Seq order;
    /// a reconnecting worker may re-send overlapping batches - the database's (RunId, Seq) primary
    /// key absorbs them, so delivery is at-least-once on the wire and exactly-once in the table.
    /// <para>
    /// The method deliberately takes no CancellationToken parameter: hub-method invocation binding
    /// counts every declared parameter against the client's argument list, and
    /// <see cref="Hub.ConnectionAborted"/> carries the same disconnect signal without one.
    /// </para>
    /// </summary>
    public async Task AppendLinesAsync(Guid runId, LogLineMessage[] lines)
    {
        var cancellationToken = Context.ConnectionAborted;
        if (lines is not { Length: > 0 })
        {
            return;
        }

        var workerIdValue = Context.User?.FindFirstValue(ZenkinsClaims.WorkerId);
        if (!Guid.TryParse(workerIdValue, out var workerId))
        {
            // Prefixed: the connection cannot gain a credential by retrying (review fix F1).
            throw new HubException(LogStreamRejection.For("The connection has no valid worker credential."));
        }

        var token = Context.Items.TryGetValue(TokenItemKey, out var stored) ? stored as string : null;
        var workerToken = await workers.AuthenticateWorkerAsync(token, cancellationToken);
        if (workerToken is null)
        {
            // The token died mid-connection (revoked, re-registered). No Context.Abort here
            // (review fix F2): aborting first turned the rejection into a transport loss the
            // worker's drain could never see, so it retried forever. The prefixed HubException
            // below reaches the client verbatim, the drain classifies it as definitive and ends;
            // a NEXT connection with the dead token is a bare 401 at negotiate time (the
            // authentication scheme), so the dead credential cannot accumulate connections.
            throw new HubException(LogStreamRejection.For("The worker token is no longer valid."));
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var run = await context.BuildRuns.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == runId,
            cancellationToken);
        if (run is null)
        {
            // Unknown or foreign run: the same answer, no existence oracle (house rule).
            throw new HubException(LogStreamRejection.For("The run does not exist in this worker's organization."));
        }

        if (run.WorkerId != workerId)
        {
            throw new HubException(LogStreamRejection.For("The run is not assigned to this worker."));
        }

        await ingest.AppendAsync(runId, workerToken.OrganizationId, lines, cancellationToken);
    }
}
