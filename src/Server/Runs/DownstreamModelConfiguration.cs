using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;

namespace Zenkins.Server.Runs;

/// <summary>
/// The EF Core model for card F-026's additions, kept next to the feature rather than growing
/// <c>ZenkinsDbContext.OnModelCreating</c> by three blocks:
/// <list type="bullet">
/// <item><see cref="ProjectLink"/> - tenant-owned, unique per (upstream, downstream) pair.</item>
/// <item><see cref="OutboxMessage"/> - tenant-owned, indexed by what the dispatcher asks ("pending, due now").</item>
/// <item><see cref="BuildRun.TriggeredByRunId"/> / <see cref="BuildRun.UpstreamHops"/> - two columns on the
/// existing run table.</item>
/// </list>
/// <para>
/// <b>Deleting a project removes its links in both directions.</b> Both foreign keys cascade, which is the schema
/// half of "no dangling triggers"; the service half is that a pending obligation whose link vanished settles as
/// <c>skipped-missing</c> rather than being dispatched to a project that is gone.
/// </para>
/// </summary>
public static class DownstreamModelConfiguration
{
    public static void ConfigureDownstream(this ModelBuilder modelBuilder, ZenkinsDbContext context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);

        modelBuilder.Entity<ProjectLink>(entity =>
        {
            entity.HasKey(link => link.Id);
            entity.Property(link => link.BranchPolicy).IsRequired().HasMaxLength(32);
            entity.Property(link => link.OnlyOnSuccess).IsRequired();

            // One edge per pair. A second row would mean two downstream runs per upstream completion, which is a
            // different rule; the unique index is the guard, not a pre-flight query.
            entity.HasIndex(link => new { link.UpstreamProjectId, link.DownstreamProjectId }).IsUnique();

            entity.HasOne(link => link.UpstreamProject).WithMany()
                .HasForeignKey(link => link.UpstreamProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(link => link.DownstreamProject).WithMany()
                .HasForeignKey(link => link.DownstreamProjectId).OnDelete(DeleteBehavior.Cascade);

            entity.HasQueryFilter(link => link.OrganizationId == context.CurrentOrganizationId);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Kind).IsRequired().HasMaxLength(64);
            entity.Property(message => message.Status).IsRequired().HasMaxLength(32);
            entity.Property(message => message.Outcome).HasMaxLength(64);
            entity.Property(message => message.Reason).HasMaxLength(500);

            // The dispatcher's own query: "pending and due", oldest first. One index, no scan.
            entity.HasIndex(message => new { message.Status, message.NextAttemptAt });

            // The run page's query: the chain this run owes and was given.
            entity.HasIndex(message => message.UpstreamRunId);

            // A run's deletion takes its obligations with it: an obligation whose cause is gone has no page to be
            // rendered on and no chain to continue.
            entity.HasOne<BuildRun>().WithMany()
                .HasForeignKey(message => message.UpstreamRunId).OnDelete(DeleteBehavior.Cascade);

            entity.HasQueryFilter(message => message.OrganizationId == context.CurrentOrganizationId);
        });
    }
}
