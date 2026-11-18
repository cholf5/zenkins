using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;

namespace Zenkins.Server.Notifications;

/// <summary>
/// The EF Core model for card F-039, next to the feature like F-026's:
/// <list type="bullet">
/// <item><see cref="NotificationEndpoint"/> - tenant-owned, cascade-deleted with its project.</item>
/// <item>Three nullable columns on the F-026 outbox that let one table carry two kinds of obligation:
/// <c>NotificationEndpointId</c>, <c>NotificationEvent</c> and the secret snapshot
/// <c>NotificationSecretCiphertext</c> - plus <c>LinkId</c>, which becomes nullable because a notification row has no
/// chain edge.</item>
/// </list>
/// <para>
/// <b>Deliberately no check constraint that "exactly one of LinkId / NotificationEndpointId is set".</b> A closed
/// vocabulary of kinds is the same guarantee one layer up, and a constraint there would have to be edited by every
/// later kind instead of merely extended.
/// </para>
/// </summary>
public static class NotificationModelConfiguration
{
    public static void ConfigureNotifications(this ModelBuilder modelBuilder, ZenkinsDbContext context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);

        modelBuilder.Entity<NotificationEndpoint>(entity =>
        {
            entity.HasKey(endpoint => endpoint.Id);
            entity.Property(endpoint => endpoint.Url).IsRequired().HasMaxLength(NotificationLimits.MaxUrlLength);
            entity.Property(endpoint => endpoint.SecretCiphertext).IsRequired();
            entity.Property(endpoint => endpoint.Events).IsRequired().HasMaxLength(64);
            entity.Property(endpoint => endpoint.LastOutcome).HasMaxLength(32);
            entity.Property(endpoint => endpoint.LastError).HasMaxLength(NotificationLimits.MaxErrorLength);
            entity.Property(endpoint => endpoint.CreatedByUserId).IsRequired().HasMaxLength(450);

            // The page and the dispatcher both ask "this project's endpoints" / "this endpoint"; one index serves
            // the first, the primary key the second.
            entity.HasIndex(endpoint => endpoint.ProjectId);

            entity.HasOne(endpoint => endpoint.Project).WithMany()
                .HasForeignKey(endpoint => endpoint.ProjectId).OnDelete(DeleteBehavior.Cascade);

            entity.HasQueryFilter(endpoint => endpoint.OrganizationId == context.CurrentOrganizationId);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.Property(message => message.NotificationEvent).HasMaxLength(32);

            // An endpoint that is removed owes nothing: its pending rows go with it rather than settling as
            // "receiver gone" on every pass.
            entity.HasOne<NotificationEndpoint>().WithMany()
                .HasForeignKey(message => message.NotificationEndpointId).OnDelete(DeleteBehavior.Cascade);

            // Both dispatchers filter by kind, so this index is what keeps each pass off the other's rows.
            entity.HasIndex(message => new { message.Kind, message.Status });
        });
    }
}
