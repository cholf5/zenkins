using Microsoft.EntityFrameworkCore;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Tests.Pipelines;

/// <summary>
/// <c>ZenkinsDbContextModelSnapshot.cs</c> describes exactly the model the context builds - the snapshot is
/// what the <i>next</i> <c>dotnet ef migrations add</c> diffs against, so a snapshot that drifted produces a
/// migration nobody asked for.
/// <para>
/// <b>Why this is a fact.</b> Generating a migration under the wrong provider silently rewrites the model
/// snapshot with that provider's store types (SQLite's <c>TEXT</c>/<c>INTEGER</c> instead of PostgreSQL's),
/// and the damage is invisible: the build stays green, the migration applies, and the <b>next</b> migration
/// then contains thousands of spurious <c>AlterColumn</c> operations. <c>docs/development.md</c> §3 warns
/// about it in prose; this fact turns the warning into a red test. It was written for card F-032, whose
/// author hit it once.
/// </para>
/// <para>
/// The context is configured for <b>PostgreSQL</b> on purpose: the committed snapshot is the PostgreSQL one
/// (development.md §3), so that is the comparison that means anything. No connection is opened -
/// <see cref="RelationalDatabaseFacadeExtensions.HasPendingModelChanges"/> diffs the model against the
/// snapshot's model, and the connection string here is never used.
/// </para>
/// <para>
/// <b>What this fact does NOT cover, deliberately.</b> Every migration also carries its own designer
/// snapshot (<c>&lt;timestamp&gt;_&lt;Name&gt;.Designer.cs</c>), and
/// <see cref="RelationalDatabaseFacadeExtensions.HasPendingModelChanges"/> looks only at the runtime model
/// versus <c>ZenkinsDbContextModelSnapshot.cs</c> - it never reads those designers. So editing a
/// migration's designer alone (changing a default in <c>BuildTargetModel</c>, say) leaves this fact
/// <b>green</b>: that is a known blind spot, not a promise. Covering it would mean loading each migration's
/// own model and diffing it with the next one through the model differ - a per-migration harness for a file
/// that only `dotnet ef` writes - and that cost is not worth paying; the point of the fact is the snapshot,
/// because that is the file a bad `migrations add` actually damages.
/// </para>
/// </summary>
[Trait("Category", "Pipeline")]
public sealed class ModelSnapshotTests
{
    [Fact]
    public void The_model_snapshot_describes_exactly_the_current_model()
    {
        var options = new DbContextOptionsBuilder<ZenkinsDbContext>()
            .UseZenkinsDatabase(new ZenkinsDataOptions(
                DatabaseProvider.Postgres,
                "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1"))
            .Options;

        using var context = new ZenkinsDbContext(options, new TenantContext(), new SystemDataScope());

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "ZenkinsDbContextModelSnapshot.cs is behind the model (or was rewritten under the wrong "
            + "provider). Regenerate the migrations with Database__Provider=Postgres "
            + "(docs/development.md §3) instead of hand-editing the snapshot.");
    }
}
