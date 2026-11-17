using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Shared.Projects;

namespace Zenkins.Server.Branches;

/// <summary>
/// The model configuration of <see cref="BranchJob"/> (card F-023). It lives next to the feature - the
/// model grows by one call in <see cref="ZenkinsDbContext"/> - the F-013/F-016/F-017 shape.
/// </summary>
public static class BranchModelConfiguration
{
    /// <summary>
    /// The branch job table: unique <c>(ProjectId, Branch)</c> (the branch is the job's key), a tenant
    /// filter, a cascade from the container project, and a check constraint on the closed state
    /// vocabulary.
    /// </summary>
    public static void ConfigureBranches(this ModelBuilder modelBuilder, ZenkinsDbContext context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);

        modelBuilder.Entity<BranchJob>(entity =>
        {
            entity.HasKey(branch => branch.Id);
            entity.Property(branch => branch.Branch).IsRequired().HasMaxLength(BranchIdentity.MaxLength);
            entity.Property(branch => branch.PipelineState).IsRequired().HasMaxLength(16);
            entity.Property(branch => branch.LastCommit).HasMaxLength(64);
            entity.Property(branch => branch.ObservedCommit).HasMaxLength(64);
            entity.Property(branch => branch.OrphanedEvidence).HasMaxLength(32);
            entity.Property(branch => branch.PendingCommit).HasMaxLength(64);
            entity.Property(branch => branch.PendingTags).IsRequired().HasMaxLength(400);

            // The job's identity. Case-sensitive on purpose: git treats `Main` and `main` as two refs and
            // so does the run snapshot this key joins to, so a case-folding collation would merge two
            // branch jobs into one. Both supported providers compare text case-sensitively here (SQLite's
            // BINARY and PostgreSQL's default collation), and the column keeps git's 255-character cap.
            entity.HasIndex(branch => new { branch.ProjectId, branch.Branch }).IsUnique();

            // The branch list reads one project's rows; discovery (F-023's interval pass) reads a whole
            // project's rows by their tenant. One index covers both.
            entity.HasIndex(branch => new { branch.OrganizationId, branch.ProjectId });

            entity.HasOne<Organization>().WithMany()
                .HasForeignKey(branch => branch.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(branch => branch.Project).WithMany()
                .HasForeignKey(branch => branch.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            // The context exposes this entity through Set<T>()/the DbSet property; the table name is
            // explicit and plural, like every other product table (F-016's precedent).
            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_BranchJobs_PipelineState",
                    "\"PipelineState\" IN ("
                    + string.Join(", ", BranchPipelineStates.All.Select(state => "'" + state + "'"))
                    + ")");

                // The orphan evidence is a closed vocabulary, and it is present exactly when the marker
                // is: a timestamp with no evidence would be an unexplained deletion claim.
                table.HasCheckConstraint(
                    "CK_BranchJobs_OrphanedEvidence",
                    "(\"OrphanedAt\" IS NULL AND \"OrphanedEvidence\" IS NULL) OR "
                    + "(\"OrphanedAt\" IS NOT NULL AND \"OrphanedEvidence\" IS NOT NULL AND \"OrphanedEvidence\" IN ("
                    + string.Join(", ", BranchOrphanEvidence.All.Select(word => "'" + word + "'"))
                    + "))");
            });

            entity.HasQueryFilter(branch => branch.OrganizationId == context.CurrentOrganizationId);
        });
    }
}
