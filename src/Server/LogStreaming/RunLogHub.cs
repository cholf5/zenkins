using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Shared.LogStreaming;

namespace Zenkins.Server.LogStreaming;

/// <summary>
/// The browser-facing run-log hub (card F-008): run detail pages subscribe to their run's group
/// and receive history chunks plus live lines. Groups are per run id; the authorization happens
/// twice - the subscription ticket authenticates the <em>connection</em> (who the caller is), and
/// <see cref="JoinRunAsync"/> re-validates against the database that this user is <em>still</em> a
/// member of the ticket's organization and that the run belongs to it. A foreign run id is
/// indistinguishable from a missing one: both are the same generic rejection, and no line ever
/// crosses an organization boundary.
/// </para>
/// </summary>
[Authorize(AuthenticationSchemes = RunLogTicketAuthenticationDefaults.AuthenticationScheme)]
public sealed class RunLogHub(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    RunLogAccess runLogAccess,
    RunLogOptions options) : Hub<IRunLogClient>
{
    private const string HistoryMethodName = nameof(IRunLogClient.HistoryLines);

    /// <summary>
    /// Joins the caller to the run's group after the membership and run-organization checks, then
    /// streams the run's persisted history in bounded chunks. Joining happens before the history
    /// read, so live lines flushed while the history streams are merged by the browser's Seq
    /// deduplication rather than lost.
    /// <para>
    /// Like the worker hub's append, the method takes no CancellationToken parameter (invocation
    /// binding counts every declared parameter against the client's argument list);
    /// <see cref="Hub.ConnectionAborted"/> carries the disconnect signal.
    /// </para>
    /// </summary>
    public async Task JoinRunAsync(Guid runId)
    {
        var cancellationToken = Context.ConnectionAborted;
        var (userId, organizationId) = RequireTicketPrincipal();

        if (!await runLogAccess.CanJoinRunAsync(userId, organizationId, runId, cancellationToken))
        {
            // Generic on purpose: unknown run, foreign run and revoked membership are the same answer.
            throw new HubException("You are not allowed to subscribe to this run's log.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, RunLogGroups.ForRun(runId), cancellationToken);

        // History in chunks: the read itself is streamed (PostgreSQL streams natively; SQLite
        // buffers one query's result, which is a bounded read of persisted rows, not a live
        // buffer), and each message carries at most HistoryChunkSize lines.
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var chunk = new List<LogLineMessage>(options.HistoryChunkSize);
        var rows = context.BuildLogs
            .AsNoTracking()
            .Where(log => log.RunId == runId)
            .OrderBy(log => log.Seq)
            .Select(log => new LogLineMessage(log.Seq, log.Stream, log.Message, log.Timestamp));
        await foreach (var line in rows.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            chunk.Add(line);
            if (chunk.Count >= options.HistoryChunkSize)
            {
                await Clients.Caller.HistoryLines(chunk);
                chunk = new List<LogLineMessage>(options.HistoryChunkSize);
            }
        }

        if (chunk.Count > 0)
        {
            await Clients.Caller.HistoryLines(chunk);
        }
    }

    private (string UserId, Guid OrganizationId) RequireTicketPrincipal()
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var organizationIdValue = Context.User?.FindFirstValue(ZenkinsClaims.OrganizationId);
        if (string.IsNullOrEmpty(userId) || !Guid.TryParse(organizationIdValue, out var organizationId))
        {
            // The ticket scheme guarantees both claims; an absent one means the connection
            // authenticated through some other path - refuse.
            throw new HubException("The connection has no valid subscription ticket.");
        }

        return (userId, organizationId);
    }
}
