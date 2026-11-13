using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Shared.Artifacts;

namespace Zenkins.Server.Artifacts;

/// <summary>
/// The model configuration of <see cref="Artifact"/> (card F-016). It lives next to the feature - the
/// model grows by one call in <see cref="ZenkinsDbContext"/> - so the artifact's storage rules are
/// reviewed in one file instead of being scattered through the context.
/// </summary>
public static class ArtifactModelConfiguration
{
    /// <summary>Adds the artifact entity: unique <c>(RunId, Name)</c>, cascade from the run, tenant filter.</summary>
    public static void ConfigureArtifacts(this ModelBuilder modelBuilder, ZenkinsDbContext context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);

        modelBuilder.Entity<Artifact>(entity =>
        {
            entity.HasKey(artifact => artifact.Id);
            entity.Property(artifact => artifact.Name).IsRequired()
                .HasMaxLength(ArtifactPathRules.MaxRelativePathLength);
            entity.Property(artifact => artifact.Kind).IsRequired().HasMaxLength(16);
            entity.Property(artifact => artifact.Status).IsRequired().HasMaxLength(16);

            // No max length on purpose: the manifest is a JSON document whose size follows the file count,
            // and the file count itself is capped (Artifacts:MaxFiles), not the column.
            entity.Property(artifact => artifact.ManifestJson);

            // The guard, not a pre-flight query: two concurrent creates cannot both pass an "is the name
            // free" check, and it is what makes artifact("bin") twice a refused pipeline bug.
            entity.HasIndex(artifact => new { artifact.RunId, artifact.Name }).IsUnique();

            // The run page reads one run's artifacts; the run's cascade removes them with it.
            entity.HasIndex(artifact => artifact.RunId);

            entity.HasOne<Organization>().WithMany().HasForeignKey(artifact => artifact.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(artifact => artifact.Run).WithMany().HasForeignKey(artifact => artifact.RunId)
                .OnDelete(DeleteBehavior.Cascade);

            // Belt and braces for the two string discriminators, same shape as the run/step constraints.
            // The table name is explicit because the context exposes the entity through Set<Artifact>()
            // rather than a DbSet property; the product's tables are plural.
            entity.ToTable("Artifacts", table =>
            {
                table.HasCheckConstraint(
                    "CK_Artifacts_Kind",
                    $"\"Kind\" IN ('{ArtifactKind.File}', '{ArtifactKind.Directory}')");
                table.HasCheckConstraint(
                    "CK_Artifacts_Status",
                    $"\"Status\" IN ('{ArtifactStatus.Pending}', '{ArtifactStatus.Ready}', "
                    + $"'{ArtifactStatus.Incomplete}')");
            });

            entity.HasQueryFilter(artifact => artifact.OrganizationId == context.CurrentOrganizationId);
        });
    }
}
