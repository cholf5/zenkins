using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;

namespace Zenkins.Server.Schedules;

/// <summary>
/// The model configuration of <see cref="ProjectSchedule"/> and <see cref="ScheduleOccurrence"/> (card F-024).
/// It lives next to the feature - the model grows by one call in <see cref="ZenkinsDbContext"/> - so the two
/// entities' storage rules are reviewed in one file (the F-013/F-016/F-017 shape).
/// </summary>
public static class ScheduleModelConfiguration
{
    /// <summary>
    /// Adds both entities: the occurrence ledger's unique <c>(ScheduleId, DueAt)</c> (the one-run-per-due-time
    /// guard), the "no two identical schedules on one project" index, tenant filters, and cascades from the
    /// organization and the project.
    /// </summary>
    public static void ConfigureSchedules(this ModelBuilder modelBuilder, ZenkinsDbContext context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);

        modelBuilder.Entity<ProjectSchedule>(entity =>
        {
            entity.HasKey(schedule => schedule.Id);
            entity.Property(schedule => schedule.Cron).IsRequired().HasMaxLength(ScheduleCron.MaxLength);
            entity.Property(schedule => schedule.TimeZone).IsRequired()
                .HasMaxLength(ScheduleErrorCodes.MaxTimeZoneLength);
            entity.Property(schedule => schedule.CreatedByUserId).IsRequired().HasMaxLength(450);

            // The same expression in the same zone twice on one project would queue two runs for every due time
            // and no operator ever means that. The index is the guard, not a pre-flight query: a second save is
            // refused with a field error even if two posts race.
            entity.HasIndex(schedule => new { schedule.ProjectId, schedule.Cron, schedule.TimeZone }).IsUnique();

            // The settings page reads one project's schedules; the tenant column is the filter's index.
            entity.HasIndex(schedule => new { schedule.OrganizationId, schedule.ProjectId });

            entity.HasOne<Organization>().WithMany()
                .HasForeignKey(schedule => schedule.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(schedule => schedule.Project).WithMany()
                .HasForeignKey(schedule => schedule.ProjectId).OnDelete(DeleteBehavior.Cascade);

            entity.ToTable("ProjectSchedules");

            entity.HasQueryFilter(schedule => schedule.OrganizationId == context.CurrentOrganizationId);
        });

        modelBuilder.Entity<ScheduleOccurrence>(entity =>
        {
            entity.HasKey(occurrence => occurrence.Id);
            entity.Property(occurrence => occurrence.DueAt).IsRequired();
            entity.Property(occurrence => occurrence.Outcome).IsRequired()
                .HasMaxLength(ScheduleOutcomes.MaxLength);
            entity.Property(occurrence => occurrence.Detail).HasMaxLength(ScheduleOutcomes.MaxDetailLength);

            // F-024's race guard: one row per (schedule, due time). Two schedulers that both decide a due time is
            // due cannot both commit a record - the loser's transaction (and the run it staged) rolls back.
            entity.HasIndex(occurrence => new { occurrence.ScheduleId, occurrence.DueAt }).IsUnique();

            // The page's read: one schedule's newest rows, and the tenant's own.
            entity.HasIndex(occurrence => new { occurrence.OrganizationId, occurrence.ScheduleId });

            entity.HasOne<Organization>().WithMany()
                .HasForeignKey(occurrence => occurrence.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(occurrence => occurrence.Schedule).WithMany()
                .HasForeignKey(occurrence => occurrence.ScheduleId).OnDelete(DeleteBehavior.Cascade);

            // Belt and braces for the folded count: a negative number of collapsed due times would make the page
            // state a total that never happened.
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_ScheduleOccurrences_CollapsedDueTimes",
                "\"CollapsedDueTimes\" >= 0"));

            entity.HasQueryFilter(occurrence => occurrence.OrganizationId == context.CurrentOrganizationId);
        });
    }
}
