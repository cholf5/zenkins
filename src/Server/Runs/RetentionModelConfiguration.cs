using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data.Entities;

namespace Zenkins.Server.Runs;

/// <summary>
/// The EF Core model for card F-038: three columns, no new table.
/// <list type="bullet">
/// <item><see cref="Project.RetentionKeep"/> / <see cref="Project.RetentionDays"/> - both nullable, because
/// <b>unset is a rule</b> ("keep everything"), not a value of the rule.</item>
/// <item><see cref="BuildRun.Pinned"/> - not nullable, defaulting to <c>false</c>, so every row written before
/// this card answers "not pinned" from the database itself and not only from an initializer.</item>
/// </list>
/// <para>
/// <b>No index on <c>Pinned</c>.</b> The policy is read per project and the candidate query already walks the
/// project's runs by their <c>(ProjectId, Number)</c> index; a second index would be maintained on every write
/// for a column only that scan reads.
/// </para>
/// </summary>
public static class RetentionModelConfiguration
{
    public static void ConfigureRetention(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Project>(entity =>
        {
            entity.Property(project => project.RetentionKeep);
            entity.Property(project => project.RetentionDays);
        });

        modelBuilder.Entity<BuildRun>(entity => entity.Property(run => run.Pinned).IsRequired());
    }
}
