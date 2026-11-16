using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;

namespace Zenkins.Server.Api;

/// <summary>
/// The model configuration of card F-027's two entities: <see cref="ApiToken"/> and
/// <see cref="ApiIdempotencyRecord"/>. It lives next to the feature (the F-013/F-016/F-017 shape), so
/// the model grows by one call in <see cref="ZenkinsDbContext"/> and the storage rules are reviewed in
/// one file.
/// </summary>
public static class ApiModelConfiguration
{
    /// <summary>
    /// Adds both entities: the globally unique token hash (the authentication lookup key), the
    /// unique <c>(OrganizationId, Name)</c> label, the unique <c>(ApiTokenId, Key)</c> idempotency
    /// guard, the tenant filters, and the cascades from the organization, the token and the run.
    /// </summary>
    public static void ConfigureApi(this ModelBuilder modelBuilder, ZenkinsDbContext context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);

        modelBuilder.Entity<ApiToken>(entity =>
        {
            entity.HasKey(token => token.Id);
            entity.Property(token => token.Name).IsRequired().HasMaxLength(ApiTokenLimits.MaxNameLength);

            // The hash is the authentication lookup key: globally unique, also the cheap index the
            // system-scope lookup uses. Plaintext is never stored or logged.
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.Property(token => token.TokenHash).IsRequired().HasMaxLength(64);
            entity.Property(token => token.Scopes).IsRequired().HasMaxLength(ApiTokenLimits.MaxScopesLength);
            entity.Property(token => token.CreatedByUserId).IsRequired().HasMaxLength(450);

            // The guard, not a pre-flight query: two concurrent mints cannot both pass an "is the name
            // free" check; the service translates the violation into a field error.
            entity.HasIndex(token => new { token.OrganizationId, token.Name }).IsUnique();

            entity.HasOne<Organization>().WithMany().HasForeignKey(token => token.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.ToTable("ApiTokens");

            // Revoking and a request's last-used stamp write the same row: the token is a concurrency
            // token so a revoke racing a touch cannot be silently overwritten (the F-004 shape).
            entity.Property(token => token.RevokedAt).IsConcurrencyToken();

            entity.HasQueryFilter(token => token.OrganizationId == context.CurrentOrganizationId);
        });

        modelBuilder.Entity<ApiIdempotencyRecord>(entity =>
        {
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Key).IsRequired()
                .HasMaxLength(ApiTokenLimits.MaxIdempotencyKeyLength);

            // The replay guard: one key per token. The database decides the winner of two concurrent
            // posts carrying the same key, exactly like the webhook dedupe index.
            entity.HasIndex(record => new { record.ApiTokenId, record.Key }).IsUnique();
            entity.HasIndex(record => record.RunId);

            entity.HasOne<Organization>().WithMany().HasForeignKey(record => record.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ApiToken>().WithMany().HasForeignKey(record => record.ApiTokenId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<BuildRun>().WithMany().HasForeignKey(record => record.RunId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.ToTable("ApiIdempotencyRecords");

            entity.HasQueryFilter(record => record.OrganizationId == context.CurrentOrganizationId);
        });
    }
}
