using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using Zenkins.Server.Auth;
using Zenkins.Server.Data.Entities;

namespace Zenkins.Tests.Auth;

/// <summary>
/// The two branches of <see cref="RegistrationService"/> that a normal SQLite run cannot reach:
/// the bootstrap write failing after the account insert (transaction must roll the account back),
/// and losing the bootstrap race (rollback, then create an account without an organization).
/// <para>
/// The real guard is exercised concurrently in
/// <see cref="BootstrapTests.Bootstrap_guard_lets_exactly_one_of_two_concurrent_attempts_create_the_organization"/>;
/// these tests substitute <see cref="IBootstrapService"/> only to reach the registration branches
/// deterministically.
/// </para>
/// </summary>
[Trait("Category", "Auth")]
public sealed class RegistrationTransactionTests
{
    private const string AliceEmail = "alice@example.test";

    [Fact]
    public async Task Registration_rolls_back_the_account_when_the_bootstrap_write_fails()
    {
        using var host = new AuthTestHost(services =>
        {
            services.RemoveAll<IBootstrapService>();
            services.AddScoped<IBootstrapService, FailingBootstrap>();
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.InScopeAsync(services => services
                .GetRequiredService<RegistrationService>()
                .RegisterAsync(AliceEmail, AuthTestHost.ValidPassword)));

        // The account insert and the bootstrap share the transaction, so a bootstrap failure also
        // removes the account. With the user committed separately this assertion is 1, not 0.
        var snapshot = await host.ReadSnapshotAsync();
        Assert.Equal(0, snapshot.Users);
        Assert.Equal(0, snapshot.Organizations);
        Assert.Equal(0, snapshot.Memberships);
        Assert.Equal(0, snapshot.BootstrapMarkers);
    }

    [Fact]
    public async Task Registration_falls_back_to_an_account_without_organization_when_the_bootstrap_loses_the_race()
    {
        using var host = new AuthTestHost(services =>
        {
            services.RemoveAll<IBootstrapService>();
            services.AddScoped<IBootstrapService, LostRaceBootstrap>();
        });

        var result = await host.InScopeAsync(services => services
            .GetRequiredService<RegistrationService>()
            .RegisterAsync(AliceEmail, AuthTestHost.ValidPassword));

        // The first transaction (with its account insert) was rolled back and the account was
        // re-created without an organization; the loser is still a working account.
        Assert.True(result.Succeeded);
        Assert.Equal(BootstrapOutcome.AlreadyCompleted, result.Bootstrap);
        Assert.Empty(result.Errors);

        var snapshot = await host.ReadSnapshotAsync();
        Assert.Equal(1, snapshot.Users);
        Assert.Equal(0, snapshot.Organizations);
        Assert.Equal(0, snapshot.Memberships);
        Assert.Equal(0, snapshot.BootstrapMarkers);
    }

    /// <summary>Fails after the account insert, inside the registration transaction.</summary>
    private sealed class FailingBootstrap : IBootstrapService
    {
        public Task<bool> HasCompletedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<BootstrapOutcome> TryCreateOwnerOrganizationAsync(
            User user,
            string? organizationName = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Injected bootstrap failure after the account insert.");
    }

    /// <summary>
    /// Reports that another transaction won the marker. A real competing winner cannot be committed
    /// on SQLite while the registration transaction already holds the write lock (the account insert),
    /// which is exactly why this branch is unreachable through the SQLite HTTP flow; the real guard
    /// is pinned by the BootstrapService-level concurrency test instead.
    /// </summary>
    private sealed class LostRaceBootstrap : IBootstrapService
    {
        public Task<bool> HasCompletedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<BootstrapOutcome> TryCreateOwnerOrganizationAsync(
            User user,
            string? organizationName = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(BootstrapOutcome.AlreadyCompleted);
    }
}
