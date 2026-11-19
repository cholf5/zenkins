using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zenkins.Server.Auth;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Tests.Auth;

/// <summary>
/// F-001's bootstrap rules at the data layer: exactly one owner organization, a marker that refuses
/// a second bootstrap even under concurrency, and a membership that only accepts a real account.
/// </summary>
[Trait("Category", "Auth")]
public sealed class BootstrapTests : IDisposable
{
    private const string AliceEmail = "alice@example.test";
    private const string BobEmail = "bob@example.test";

    private readonly AuthTestHost _host = new();

    [Fact]
    public async Task First_registration_creates_one_user_organization_owner_membership_and_marker()
    {
        var result = await RegisterAsync(AliceEmail);

        Assert.True(result.Succeeded);
        Assert.Equal(BootstrapOutcome.Created, result.Bootstrap);
        Assert.Empty(result.Errors);

        var snapshot = await _host.ReadSnapshotAsync();
        Assert.Equal(1, snapshot.Users);
        Assert.Equal(1, snapshot.Organizations);
        Assert.Equal(1, snapshot.Memberships);
        Assert.Equal(1, snapshot.OwnerMemberships);
        Assert.Equal(1, snapshot.BootstrapMarkers);
        Assert.Equal(snapshot.UserIds, snapshot.MembershipUserIds);
    }

    [Fact]
    public async Task Bootstrap_insert_is_refused_when_the_marker_already_exists()
    {
        var first = await RegisterAsync(AliceEmail);
        Assert.Equal(BootstrapOutcome.Created, first.Bootstrap);

        // Bypass the registration pre-check on purpose: this is the raw bootstrap attempt that a
        // racing second first-registration would make, and the unique marker key is the only guard.
        var secondOutcome = await _host.InScopeAsync(async services =>
        {
            await using var context = await services
                .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync();
            var user = await context.Users.SingleAsync();

            var bootstrap = services.GetRequiredService<BootstrapService>();
            return await bootstrap.TryCreateOwnerOrganizationAsync(user);
        });

        Assert.Equal(BootstrapOutcome.AlreadyCompleted, secondOutcome);

        var snapshot = await _host.ReadSnapshotAsync();
        Assert.Equal(1, snapshot.Users);
        Assert.Equal(1, snapshot.Organizations);
        Assert.Equal(1, snapshot.Memberships);
        Assert.Equal(1, snapshot.BootstrapMarkers);
    }

    [Fact]
    public async Task Second_registration_creates_an_account_without_an_organization()
    {
        Assert.True((await RegisterAsync(AliceEmail)).Succeeded);

        var second = await RegisterAsync(BobEmail);

        Assert.True(second.Succeeded);
        Assert.Equal(BootstrapOutcome.NotAttempted, second.Bootstrap);

        var snapshot = await _host.ReadSnapshotAsync();
        Assert.Equal(2, snapshot.Users);
        Assert.Equal(1, snapshot.Organizations);
        Assert.Equal(1, snapshot.Memberships);
        Assert.Equal(1, snapshot.BootstrapMarkers);
        Assert.Single(snapshot.MembershipUserIds);
    }

    [Fact]
    public async Task Bootstrap_stays_closed_after_the_first_account_is_deleted()
    {
        Assert.Equal(BootstrapOutcome.Created, (await RegisterAsync(AliceEmail)).Bootstrap);
        Assert.True((await RegisterAsync(BobEmail)).Succeeded);

        // Simulate an operator removing the original Owner. The starter organization survives (its
        // membership cascades away with the account), and the marker must survive too.
        await _host.InScopeAsync(async services =>
        {
            await using var context = await services
                .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync();
            context.Users.Remove(await context.Users.SingleAsync(user => user.UserName == AliceEmail));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });

        var outcome = await _host.InScopeAsync(async services =>
        {
            await using var context = await services
                .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync();
            var bob = await context.Users.SingleAsync(user => user.UserName == BobEmail);

            var bootstrap = services.GetRequiredService<BootstrapService>();
            return await bootstrap.TryCreateOwnerOrganizationAsync(bob);
        });

        Assert.Equal(BootstrapOutcome.AlreadyCompleted, outcome);

        var snapshot = await _host.ReadSnapshotAsync();
        Assert.Equal(1, snapshot.Users);
        Assert.Equal(1, snapshot.Organizations);
        Assert.Equal(0, snapshot.Memberships);
        Assert.Equal(1, snapshot.BootstrapMarkers);
    }

    [Fact]
    public async Task Bootstrap_guard_lets_exactly_one_of_two_concurrent_attempts_create_the_organization()
    {
        // Seed the two accounts outside the race; no bootstrap has happened yet. (Creating them via
        // RegisterAsync would already complete the bootstrap.)
        await SeedAccountsAsync("race-a", "race-b");

        var cancellationToken = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;

        // Two independent DI scopes = two connections, both parked immediately before the guard and
        // then racing into it. TryCreateOwnerOrganizationAsync always attempts the marker insert, so
        // exactly one SaveChanges can commit it; the other must hit the unique constraint. (SQLite
        // serializes the two implicit SaveChanges transactions, which is why the winner is
        // deterministic in time but the outcome - one Created, one AlreadyCompleted - is the same.)
        async Task<BootstrapOutcome> AttemptAsync(string userId)
        {
            await using var scope = _host.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ZenkinsDbContext>();
            var user = await context.Users.SingleAsync(
                candidate => candidate.Id == userId,
                cancellationToken);

            if (Interlocked.Increment(ref arrivals) == 2)
            {
                gate.SetResult();
            }

            await gate.Task;

            return await scope.ServiceProvider
                .GetRequiredService<BootstrapService>()
                .TryCreateOwnerOrganizationAsync(user, organizationName: null, cancellationToken);
        }

        var outcomes = await Task.WhenAll(
            Task.Run(() => AttemptAsync("race-a"), cancellationToken),
            Task.Run(() => AttemptAsync("race-b"), cancellationToken));

        Assert.Equal(1, outcomes.Count(outcome => outcome == BootstrapOutcome.Created));
        Assert.Equal(1, outcomes.Count(outcome => outcome == BootstrapOutcome.AlreadyCompleted));

        var snapshot = await _host.ReadSnapshotAsync();
        Assert.Equal(2, snapshot.Users);
        Assert.Equal(1, snapshot.Organizations);
        Assert.Equal(1, snapshot.Memberships);
        Assert.Equal(1, snapshot.OwnerMemberships);
        Assert.Equal(1, snapshot.BootstrapMarkers);
    }

    [Fact]
    public async Task Membership_requires_an_existing_user()
    {
        var exception = await _host.InScopeAsync(async services =>
        {
            using var systemScope = services.GetRequiredService<ISystemDataScope>().BeginScope();
            await using var context = await services
                .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync();

            var organization = new Organization { Name = "Orphan Org" };
            context.Organizations.Add(organization);
            context.OrganizationMembers.Add(new OrganizationMember
            {
                OrganizationId = organization.Id,
                UserId = "no-such-user",
                Role = OrganizationRole.Owner,
            });

            return await Record.ExceptionAsync(() =>
                context.SaveChangesAsync(TestContext.Current.CancellationToken));
        });

        // F-001's OrganizationMember.UserId -> AspNetUsers foreign key, enforced by SQLite only
        // because the provider now asks for PRAGMA foreign_keys (see UseZenkinsDatabase).
        var updateException = Assert.IsType<DbUpdateException>(exception);
        var sqlite = Assert.IsType<SqliteException>(updateException.InnerException);
        Assert.Contains("FOREIGN KEY constraint failed", sqlite.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Direct_registration_rejects_a_password_below_the_configured_policy()
    {
        var result = await RegisterAsync("weak@example.test", "alllowercase123");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Contains("uppercase", StringComparison.OrdinalIgnoreCase));

        var snapshot = await _host.ReadSnapshotAsync();
        Assert.Equal(0, snapshot.Users);
        Assert.Equal(0, snapshot.Organizations);
        Assert.Equal(0, snapshot.BootstrapMarkers);
    }

    private Task<RegistrationResult> RegisterAsync(string email, string password = AuthTestHost.ValidPassword) =>
        _host.InScopeAsync(services =>
            services.GetRequiredService<RegistrationService>().RegisterAsync(email, password));

    /// <summary>Creates accounts directly, i.e. two users with no bootstrap marker.</summary>
    private Task SeedAccountsAsync(params string[] userIds) =>
        _host.InScopeAsync(async services =>
        {
            await using var context = await services
                .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync(TestContext.Current.CancellationToken);

            foreach (var userId in userIds)
            {
                context.Users.Add(new User
                {
                    Id = userId,
                    UserName = userId,
                    NormalizedUserName = userId.ToUpperInvariant(),
                    SecurityStamp = $"seed-{userId}",
                });
            }

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });

    public void Dispose() => _host.Dispose();
}
