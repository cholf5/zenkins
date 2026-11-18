using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Shared.Notifications;

namespace Zenkins.Server.Notifications;

/// <summary>
/// Stages the notification deliveries one terminal run owes (card F-039), <b>on the caller's context</b>, so they
/// commit with the terminal status itself - the same construction card F-026 used for downstream triggers, and for
/// the same reason: a Server that dies between "the run ended" and "the notification was sent" must leave the
/// obligation on disk, not in a stack frame.
/// <para>
/// <b>The event is decided here.</b> <c>recovered</c> is not a flavour of <c>success</c> that dispatch can add later:
/// it depends on the previous terminal run of the same project and branch, and that run can be trimmed by retention
/// (F-038) before the delivery happens. So the event is computed at the terminal moment and stored on the row.
/// </para>
/// <para>
/// <b>Only success, failure and recovery notify.</b> A canceled or lost run stages nothing: the card's vocabulary is
/// those three events, and a <c>Lost</c> run's "result unknown" is not a result to announce. It does still count as a
/// failure for the <em>next</em> run's recovery decision - that is the honest reading of "it was red".
/// </para>
/// </summary>
public static class CompletionNotifications
{
    /// <summary>Adds one outbox row per subscribed, enabled endpoint of <paramref name="run"/>'s project. Does not save.</summary>
    public static async Task<int> StageAsync(
        ZenkinsDbContext context,
        BuildRun run,
        string terminalStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(run);

        var eventName = await DeriveEventAsync(context, run, terminalStatus, cancellationToken);
        if (eventName is null)
        {
            return 0;
        }

        var endpoints = await context.Set<NotificationEndpoint>()
            .AsNoTracking()
            .Where(endpoint => endpoint.ProjectId == run.ProjectId && endpoint.Enabled)
            .ToListAsync(cancellationToken);
        if (endpoints.Count == 0)
        {
            return 0;
        }

        var staged = 0;
        foreach (var endpoint in endpoints)
        {
            if (!NotificationEvents.Matches(endpoint.Events, eventName))
            {
                continue;
            }

            context.OutboxMessages.Add(new OutboxMessage
            {
                OrganizationId = run.OrganizationId,
                Kind = OutboxKinds.CompletionNotification,
                UpstreamRunId = run.Id,
                Status = OutboxStatuses.Pending,
                NotificationEndpointId = endpoint.Id,
                NotificationEvent = eventName,

                // The secret in force *now*: a rotation after this point must not re-sign a delivery the receiver is
                // already expecting under the secret it holds.
                NotificationSecretCiphertext = endpoint.SecretCiphertext,
            });
            staged++;
        }

        return staged;
    }

    /// <summary>
    /// The event a run's terminal state means, or <see langword="null"/> when it means no event at all.
    /// <para>
    /// <c>recovered</c> iff this run succeeded and the previous <b>terminal</b> run of the same project and branch
    /// ended <c>Failed</c> or <c>Lost</c>. Terminal, not merely previous: a run that is still queued or running
    /// between the two says nothing about whether the branch was red, and skipping it reads the last known result.
    /// </para>
    /// </summary>
    public static async Task<string?> DeriveEventAsync(
        ZenkinsDbContext context,
        BuildRun run,
        string terminalStatus,
        CancellationToken cancellationToken)
    {
        if (string.Equals(terminalStatus, BuildRunStatus.Failed, StringComparison.Ordinal))
        {
            return NotificationEvents.Failed;
        }

        if (!string.Equals(terminalStatus, BuildRunStatus.Success, StringComparison.Ordinal))
        {
            return null;
        }

        var previousStatus = await context.BuildRuns
            .AsNoTracking()
            .Where(candidate => candidate.ProjectId == run.ProjectId
                && candidate.GitBranch == run.GitBranch
                && candidate.Id != run.Id
                && candidate.Number < run.Number
                && (candidate.Status == BuildRunStatus.Success
                    || candidate.Status == BuildRunStatus.Failed
                    || candidate.Status == BuildRunStatus.Canceled
                    || candidate.Status == BuildRunStatus.Lost))
            .OrderByDescending(candidate => candidate.Number)
            .Select(candidate => candidate.Status)
            .FirstOrDefaultAsync(cancellationToken);

        var wasRed = previousStatus is BuildRunStatus.Failed or BuildRunStatus.Lost;
        return wasRed ? NotificationEvents.Recovered : NotificationEvents.Success;
    }
}
