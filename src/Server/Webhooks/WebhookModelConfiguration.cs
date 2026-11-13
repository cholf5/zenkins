using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Runs;

namespace Zenkins.Server.Webhooks;

/// <summary>
/// The model configuration of <see cref="Webhook"/> and <see cref="WebhookDelivery"/> (card F-017). It
/// lives next to the feature - the model grows by one call in <see cref="ZenkinsDbContext"/> - so the two
/// entities' storage rules are reviewed in one file (the F-013/F-016 shape).
/// </summary>
public static class WebhookModelConfiguration
{
    /// <summary>
    /// Adds both entities: unique <c>(ProjectId, Name)</c> for the webhook, unique
    /// <c>(WebhookId, DedupeKey)</c> for the delivery record (the replay guard), tenant filters, and
    /// cascades from the project and from the webhook.
    /// </summary>
    public static void ConfigureWebhooks(this ModelBuilder modelBuilder, ZenkinsDbContext context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);

        modelBuilder.Entity<Webhook>(entity =>
        {
            entity.HasKey(webhook => webhook.Id);
            entity.Property(webhook => webhook.Name).IsRequired().HasMaxLength(WebhookValidation.MaxNameLength);

            // No max length on purpose: the ciphertext's length follows the plaintext (Data Protection
            // output) and the plaintext is generated, never typed.
            entity.Property(webhook => webhook.SecretCiphertext).IsRequired();

            entity.Property(webhook => webhook.BranchFilter).IsRequired()
                .HasMaxLength(WebhookBranchFilter.MaxFilterLength);
            entity.Property(webhook => webhook.RequiredTags).IsRequired()
                .HasMaxLength(RunValidation.MaxRequiredTagsLength);
            entity.Property(webhook => webhook.CreatedByUserId).IsRequired().HasMaxLength(450);
            entity.Property(webhook => webhook.LastDeliveryOutcome)
                .HasMaxLength(WebhookOutcomes.MaxLength);

            // The guard, not a pre-flight query: two concurrent creates cannot both pass an "is the name
            // free" check, and a duplicate name is a 400 with a field error rather than an ambiguous list.
            entity.HasIndex(webhook => new { webhook.ProjectId, webhook.Name }).IsUnique();

            // The settings page reads one project's webhooks; the tenant column is the filter's index.
            entity.HasIndex(webhook => new { webhook.OrganizationId, webhook.ProjectId });

            entity.HasOne<Organization>().WithMany().HasForeignKey(webhook => webhook.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(webhook => webhook.Project).WithMany().HasForeignKey(webhook => webhook.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            // The context exposes these entities through Set<T>() rather than DbSet properties, so the
            // table names are explicit - and plural, like every other product table (F-016's precedent).
            entity.ToTable("Webhooks");

            entity.HasQueryFilter(webhook => webhook.OrganizationId == context.CurrentOrganizationId);
        });

        modelBuilder.Entity<WebhookDelivery>(entity =>
        {
            entity.HasKey(delivery => delivery.Id);
            entity.Property(delivery => delivery.DedupeKey).IsRequired().HasMaxLength(128);
            entity.Property(delivery => delivery.Outcome).IsRequired().HasMaxLength(WebhookOutcomes.MaxLength);
            entity.Property(delivery => delivery.ReceivedAt).IsRequired();

            // The replay guard. Both halves are bounded hashes, so the key is a fixed 71 characters
            // ("hash:" + 64 hex) or 67 ("id:" + 64 hex).
            entity.HasIndex(delivery => new { delivery.WebhookId, delivery.DedupeKey }).IsUnique();

            entity.HasOne<Organization>().WithMany().HasForeignKey(delivery => delivery.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(delivery => delivery.Webhook).WithMany()
                .HasForeignKey(delivery => delivery.WebhookId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.ToTable("WebhookDeliveries");

            entity.HasQueryFilter(delivery => delivery.OrganizationId == context.CurrentOrganizationId);
        });
    }
}
