using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Secrets;

namespace Zenkins.Server.Git;

/// <summary>
/// The EF Core model for card F-013's two schema additions, kept next to the feature instead of
/// growing <c>ZenkinsDbContext.OnModelCreating</c> by two blocks:
/// <list type="bullet">
/// <item><see cref="GitCredential"/> - tenant-owned, name-unique, query-filtered like every other
/// organization row.</item>
/// <item><see cref="BuildRun.GitCommit"/> - a bounded column (a SHA-256 object name is 64 hex
/// characters) on the existing run table.</item>
/// </list>
/// <para>
/// <b>Why the bounded column matters.</b> An unconfigured <c>string</c> becomes <c>text</c> under
/// PostgreSQL; a commit is a fixed-shape identifier, so <c>character varying(64)</c> is both honest and
/// cheap to index later. The migration is generated with the PostgreSQL provider (the repository's one
/// migration set), so this configuration is what the contract gate compares against.
/// </para>
/// <para>
/// <b>Tenant filter.</b> The predicate references the <c>ZenkinsDbContext</c> instance EF rewrites to
/// the querying context, exactly like the filters configured in <c>OnModelCreating</c> itself; a
/// credential of another organization is therefore invisible, and a foreign id is a plain "not found".
/// </para>
/// </summary>
public static class GitCredentialModelConfiguration
{
    /// <summary>Applies both F-013 model additions. Called once from <c>ZenkinsDbContext</c>.</summary>
    public static void ConfigureGitCredentials(this ModelBuilder modelBuilder, ZenkinsDbContext context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);

        modelBuilder.Entity<GitCredential>(entity =>
        {
            entity.HasKey(credential => credential.Id);
            entity.Property(credential => credential.Name).IsRequired()
                .HasMaxLength(SecretValidation.MaxNameLength);
            entity.Property(credential => credential.Kind).IsRequired().HasMaxLength(16);

            // No max length on purpose: the ciphertext's length follows the plaintext (Data
            // Protection output); the form limit, not the column, bounds the value.
            entity.Property(credential => credential.Ciphertext).IsRequired();
            entity.Property(credential => credential.UserName).HasMaxLength(
                GitCredentialValidation.MaxUserNameLength);
            entity.Property(credential => credential.CreatedByUserId).IsRequired().HasMaxLength(450);

            // The guard, not a pre-flight query: two concurrent creates cannot both pass an
            // "is the name free" check. The service translates the violation into a field error.
            entity.HasIndex(credential => new { credential.OrganizationId, credential.Name }).IsUnique();
            entity.HasOne<Organization>().WithMany()
                .HasForeignKey(credential => credential.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);

            // Belt and braces for the kind text, same shape as the other string discriminators.
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_GitCredentials_Kind",
                $"\"Kind\" IN ('{GitCredentialKindTexts.HttpsToken}', '{GitCredentialKindTexts.SshKey}')"));

            entity.HasQueryFilter(credential => credential.OrganizationId == context.CurrentOrganizationId);
        });

        modelBuilder.Entity<BuildRun>(entity =>
        {
            entity.Property(run => run.GitCommit).HasMaxLength(GitCommitValidation.MaxLength);
        });
    }
}
