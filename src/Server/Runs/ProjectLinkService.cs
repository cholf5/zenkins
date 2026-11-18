using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;

namespace Zenkins.Server.Runs;

/// <summary>One chain edge as the page and the forms read it.</summary>
/// <param name="Id">The link's own id (what a delete names).</param>
/// <param name="UpstreamProjectId">The project whose run finishing causes the trigger.</param>
/// <param name="UpstreamProjectName">Its name, for a label a person recognises.</param>
/// <param name="DownstreamProjectId">The project that is queued.</param>
/// <param name="DownstreamProjectName">Its name.</param>
/// <param name="OnlyOnSuccess">Whether a failed upstream still triggers (false) or does not (true).</param>
/// <param name="BranchPolicy">One of <see cref="ProjectLinkBranchPolicies"/>.</param>
public sealed record ProjectLinkView(
    Guid Id,
    Guid UpstreamProjectId,
    string UpstreamProjectName,
    Guid DownstreamProjectId,
    string DownstreamProjectName,
    bool OnlyOnSuccess,
    string BranchPolicy);

/// <summary>Outcome of a chain edit (card F-026).</summary>
public enum ProjectLinkStatus
{
    Success = 0,
    NotFound = 1,
    InvalidInput = 2,
}

/// <summary>One refusal, with the sentence the page renders (and, for a cycle, the path that would close it).</summary>
public sealed record ProjectLinkResult(ProjectLinkStatus Status, ProjectLinkView? Link = null, string? Message = null)
{
    public static ProjectLinkResult Success(ProjectLinkView link) => new(ProjectLinkStatus.Success, link);

    public static ProjectLinkResult NotFound() => new(ProjectLinkStatus.NotFound);

    public static ProjectLinkResult Invalid(string message) =>
        new(ProjectLinkStatus.InvalidInput, null, message);
}

/// <summary>
/// The upstream/downstream edges of one organization (card F-026), and the save-time half of the loop protection.
/// <para>
/// <b>A cycle is refused here, not discovered at run time.</b> Saving a link runs a depth-first search from the new
/// downstream back towards the new upstream; if the upstream is reachable, the link would close a loop and is
/// refused with the path (<c>A -&gt; B -&gt; A</c>), because "that would make a cycle" without the cycle is a
/// message a person cannot act on. This search does not read branch policies - an edge exists whatever branches it
/// builds - so <c>A -&gt; B -&gt; A</c> is refused however the two edges choose them, and a branch policy cannot hide
/// a loop from it. What the runtime hop counter bounds is depth: the 21st link of a chain with no cycle in it stops
/// with its own reason, and it is the only stop left for an edge that reached the table without passing this check.
/// </para>
/// <para>
/// <b>Tenant boundary.</b> Every read goes through the tenant-filtered context, so a foreign project id is the same
/// answer as a missing one, and the links of another organization are invisible.
/// </para>
/// </summary>
public sealed class ProjectLinkService(
    IDbContextFactory<ZenkinsDbContext> contextFactory,
    ILogger<ProjectLinkService> logger)
{
    /// <summary>How many hops a chain may take before it stops (the runtime half of the loop protection).</summary>
    public const int MaxUpstreamHops = 20;

    /// <summary>The edges that name <paramref name="projectId"/> in either direction.</summary>
    public async Task<IReadOnlyList<ProjectLinkView>> GetForProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var links = await context.ProjectLinks
            .AsNoTracking()
            .Where(link => link.UpstreamProjectId == projectId || link.DownstreamProjectId == projectId)
            .ToListAsync(cancellationToken);

        return await ToViewsAsync(context, links, cancellationToken);
    }

    /// <summary>
    /// Declares one edge. <paramref name="direction"/> is <c>downstream</c> when
    /// <paramref name="projectId"/>'s success should run <paramref name="otherProjectId"/>, and <c>upstream</c>
    /// when <paramref name="otherProjectId"/>'s success should run <paramref name="projectId"/> - the same row
    /// either way, written from the side the form was on.
    /// </summary>
    public async Task<ProjectLinkResult> SaveAsync(
        Guid projectId,
        Guid otherProjectId,
        bool isDownstream,
        bool onlyOnSuccess,
        string? branchPolicy,
        CancellationToken cancellationToken = default)
    {
        if (projectId == otherProjectId)
        {
            return ProjectLinkResult.Invalid("A project cannot be linked to itself.");
        }

        var policy = string.IsNullOrWhiteSpace(branchPolicy) ? ProjectLinkBranchPolicies.Default : branchPolicy.Trim();
        if (!ProjectLinkBranchPolicies.IsValid(policy))
        {
            return ProjectLinkResult.Invalid(
                $"Branch policy must be one of {string.Join(", ", ProjectLinkBranchPolicies.All)}.");
        }

        var upstreamId = isDownstream ? projectId : otherProjectId;
        var downstreamId = isDownstream ? otherProjectId : projectId;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var projects = await context.Projects
            .AsNoTracking()
            .Where(project => project.Id == upstreamId || project.Id == downstreamId)
            .Select(project => project.Id)
            .ToListAsync(cancellationToken);
        if (projects.Count != 2)
        {
            // A foreign or missing id is the same answer: nothing here names both projects.
            return ProjectLinkResult.NotFound();
        }

        if (await context.ProjectLinks.AnyAsync(
                link => link.UpstreamProjectId == upstreamId && link.DownstreamProjectId == downstreamId,
                cancellationToken))
        {
            return ProjectLinkResult.Invalid("Those two projects are already linked in that direction.");
        }

        // The graph check: would the new edge make the new upstream reachable from the new downstream?
        var cycle = await FindPathAsync(context, from: downstreamId, to: upstreamId, cancellationToken);
        if (cycle is not null)
        {
            return ProjectLinkResult.Invalid(
                $"That link would close a cycle: {string.Join(" -> ", cycle)} -> "
                + $"{(await ProjectNameAsync(context, downstreamId, cancellationToken))}.");
        }

        var link = new ProjectLink
        {
            // The tenant comes from the active request; the data layer rejects anything else.
            OrganizationId = (await context.Projects.AsNoTracking()
                .Where(project => project.Id == upstreamId)
                .Select(project => project.OrganizationId)
                .SingleAsync(cancellationToken)),
            UpstreamProjectId = upstreamId,
            DownstreamProjectId = downstreamId,
            OnlyOnSuccess = onlyOnSuccess,
            BranchPolicy = policy,
        };
        context.ProjectLinks.Add(link);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception, "ProjectLinks"))
        {
            // The unique index is the guard, not the pre-flight query above: two concurrent saves cannot both win.
            return ProjectLinkResult.Invalid("Those two projects are already linked in that direction.");
        }

        logger.LogInformation(
            "Project link added. OrganizationId={OrganizationId} Upstream={UpstreamProjectId} Downstream={DownstreamProjectId} Policy={Policy} OnlyOnSuccess={OnlyOnSuccess}",
            link.OrganizationId,
            link.UpstreamProjectId,
            link.DownstreamProjectId,
            link.BranchPolicy,
            link.OnlyOnSuccess);

        var views = await ToViewsAsync(context, [link], cancellationToken);
        return ProjectLinkResult.Success(views[0]);
    }

    /// <summary>
    /// Removes one edge of <paramref name="projectId"/> (either end). A link of another organization - or one
    /// that does not touch this project - is not found, like every other tenant-owned row: the route the form
    /// POSTs to names the project, so an id lifted from elsewhere answers the same thing as a missing one.
    /// </summary>
    public async Task<bool> DeleteAsync(
        Guid projectId,
        Guid linkId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var link = await context.ProjectLinks.SingleOrDefaultAsync(
            candidate => candidate.Id == linkId
                && (candidate.UpstreamProjectId == projectId || candidate.DownstreamProjectId == projectId),
            cancellationToken);
        if (link is null)
        {
            return false;
        }

        context.ProjectLinks.Remove(link);
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Project link removed. OrganizationId={OrganizationId} Upstream={UpstreamProjectId} Downstream={DownstreamProjectId}",
            link.OrganizationId,
            link.UpstreamProjectId,
            link.DownstreamProjectId);
        return true;
    }

    /// <summary>
    /// The path from <paramref name="from"/> to <paramref name="to"/> along the downstream direction, or
    /// <see langword="null"/> when there is none. Breadth-first over the organization's edges, bounded by
    /// <see cref="MaxUpstreamHops"/> so a corrupted row set cannot make the check run away.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> FindPathAsync(
        ZenkinsDbContext context,
        Guid from,
        Guid to,
        CancellationToken cancellationToken)
    {
        var edges = await context.ProjectLinks
            .AsNoTracking()
            .Select(link => new { link.UpstreamProjectId, link.DownstreamProjectId })
            .ToListAsync(cancellationToken);

        var names = await context.Projects
            .AsNoTracking()
            .Select(project => new { project.Id, project.Name })
            .ToDictionaryAsync(project => project.Id, project => project.Name, cancellationToken);

        var previous = new Dictionary<Guid, Guid>();
        var queue = new Queue<Guid>();
        var visited = new HashSet<Guid> { from };
        queue.Enqueue(from);

        while (queue.Count > 0 && visited.Count <= MaxUpstreamHops)
        {
            var current = queue.Dequeue();
            foreach (var edge in edges.Where(edge => edge.UpstreamProjectId == current))
            {
                if (edge.DownstreamProjectId == to)
                {
                    var path = new List<string> { Label(names, to) };
                    for (var step = current; ; step = previous[step])
                    {
                        path.Insert(0, Label(names, step));
                        if (!previous.ContainsKey(step))
                        {
                            break;
                        }
                    }

                    return path;
                }

                if (visited.Add(edge.DownstreamProjectId))
                {
                    previous[edge.DownstreamProjectId] = current;
                    queue.Enqueue(edge.DownstreamProjectId);
                }
            }
        }

        return null;
    }

    private static string Label(IReadOnlyDictionary<Guid, string> names, Guid id) =>
        names.TryGetValue(id, out var name) ? name : id.ToString();

    private static Task<string> ProjectNameAsync(ZenkinsDbContext context, Guid projectId, CancellationToken ct) =>
        context.Projects.AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => project.Name)
            .SingleAsync(ct);

    private static async Task<IReadOnlyList<ProjectLinkView>> ToViewsAsync(
        ZenkinsDbContext context,
        IReadOnlyList<ProjectLink> links,
        CancellationToken cancellationToken)
    {
        var ids = links
            .SelectMany(link => new[] { link.UpstreamProjectId, link.DownstreamProjectId })
            .Distinct()
            .ToArray();
        var names = await context.Projects
            .AsNoTracking()
            .Where(project => ids.Contains(project.Id))
            .Select(project => new { project.Id, project.Name })
            .ToDictionaryAsync(project => project.Id, project => project.Name, cancellationToken);

        return
        [
            .. links.Select(link => new ProjectLinkView(
                link.Id,
                link.UpstreamProjectId,
                Label(names, link.UpstreamProjectId),
                link.DownstreamProjectId,
                Label(names, link.DownstreamProjectId),
                link.OnlyOnSuccess,
                link.BranchPolicy)),
        ];
    }
}
