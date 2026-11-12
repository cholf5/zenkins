using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Workers;
using Zenkins.Shared.Tenancy;

// The worker project's namespace is Zenkins.Worker, which shadows the entity type name inside any
// Zenkins.* namespace; the alias keeps the entity readable here.
using WorkerEntity = Zenkins.Server.Data.Entities.Worker;

namespace Zenkins.Tests.Workers;

/// <summary>
/// M1 (review fix): the registration token burn (<c>UsedAt</c>, plus <c>RevokedAt</c>) is an EF
/// concurrency token, and <see cref="WorkerService.RegisterAsync"/> must map the loser of that
/// race - a <see cref="DbUpdateConcurrencyException"/> raised when a concurrent request already
/// burned the token between its read and its write - to <see cref="WorkerOperationStatus.InvalidToken"/>
/// (401), not to a 500 or a transport-style retry.
/// <para>
/// The SQLite-backed HTTP race in <see cref="WorkerEndpointTests"/> proves the end-to-end outcome
/// but cannot force the concurrency exception deterministically (SQLite serializes the writers, so
/// the second request often simply re-reads the burned token and gets the regular 401). This test
/// pins the mapping itself by constructing the exception directly through a save interceptor.
/// </para>
/// <para>
/// B (second review fix) extends the same pinning to the Owner-side revocation:
/// <see cref="WorkerService.RevokeRegistrationTokenAsync"/> must also catch the concurrency
/// exception (revoke racing a revoke, or a revoke racing the burn) and map it to the same friendly
/// outcomes the pre-checks produce - never a 500.
/// </para>
/// </summary>
[Trait("Category", "Workers")]
public sealed class WorkerRegistrationConcurrencyTests : IDisposable
{
    private static readonly Guid OrganizationId = new("c0000000-0000-4000-8000-0000000000c1");

    /// <summary>Arbitrary acting-user id; <see cref="WorkerService"/> only string-compares it.</summary>
    private const string OwnerUserId = "revoke-owner";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"zenkins-f004-concurrency-{Guid.NewGuid():N}.db");
    private readonly TenantContext _tenantContext = new();
    private readonly SystemDataScope _systemDataScope = new();

    public WorkerRegistrationConcurrencyTests()
    {
        // The product's real migration set, on a throwaway SQLite file (same shape TwoOrgsFixture
        // uses); the test only needs Organizations/WorkerTokens/Workers.
        using var context = CreateContext();
        context.Database.Migrate();
    }

    [Fact]
    public async Task A_lost_burn_race_is_mapped_to_401_and_writes_nothing()
    {
        var plaintext = WorkerTokenCrypto.Generate(WorkerTokenCrypto.RegistrationPrefix);
        using (var scope = _systemDataScope.BeginScope())
        {
            await using var context = CreateContext();
            context.Organizations.Add(new Organization { Id = OrganizationId, Name = "Race" });
            context.WorkerTokens.Add(new WorkerToken
            {
                OrganizationId = OrganizationId,
                Kind = WorkerTokenKind.Registration,
                TokenHash = WorkerTokenCrypto.Hash(plaintext),
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Every SaveChanges this service attempts throws the exception the provider raises when a
        // concurrent registration won the burn race - so the mapping, not the storage engine,
        // is what the assertion observes.
        var service = new WorkerService(
            new SingleContextFactory(() => CreateContext(new BurningRaceInterceptor())),
            _tenantContext,
            _systemDataScope,
            NullLogger<WorkerService>.Instance);

        var result = await service.RegisterAsync(
            plaintext,
            new WorkerRegistrationInput(
                Name: "race-agent",
                Os: "Linux",
                Architecture: "X64",
                Version: "1.0.0",
                Tags: "dotnet",
                CpuCount: 4,
                MaxConcurrency: 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(WorkerOperationStatus.InvalidToken, result.Status);
        Assert.Null(result.ErrorCode);
        Assert.Null(result.WorkerId);
        Assert.Null(result.WorkerToken);

        // Nothing was written: no worker row, registration token still unused (the whole
        // SaveChanges rolled back).
        using (var scope = _systemDataScope.BeginScope())
        {
            await using var context = CreateContext();
            Assert.Empty(await context.SystemQueryAsync<WorkerEntity>(
                _ => true, TestContext.Current.CancellationToken));
            var token = Assert.Single(await context.SystemQueryAsync<WorkerToken>(
                _ => true, TestContext.Current.CancellationToken));
            Assert.Null(token.UsedAt);
        }
    }

    private ZenkinsDbContext CreateContext(SaveChangesInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<ZenkinsDbContext>()
            .UseZenkinsDatabase(new ZenkinsDataOptions(DatabaseProvider.Sqlite, $"Data Source={_databasePath}"));
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new ZenkinsDbContext(options.Options, _tenantContext, _systemDataScope);
    }

    /// <summary>
    /// Seeds one organization with an Owner and one pending registration token (through a plain,
    /// un-intercepted context) and selects the organization on the tenant context - the state
    /// <see cref="WorkerService.RevokeRegistrationTokenAsync"/> needs to reach its save.
    /// </summary>
    private async Task<Guid> SeedOwnerAndPendingTokenAsync()
    {
        using var scope = _systemDataScope.BeginScope();
        await using var context = CreateContext();
        context.Organizations.Add(new Organization { Id = OrganizationId, Name = "Revoke race" });
        // OrganizationMember.UserId has a real foreign key, so the Owner needs a user row.
        context.Users.Add(new User
        {
            Id = OwnerUserId,
            UserName = OwnerUserId,
            NormalizedUserName = OwnerUserId.ToUpperInvariant(),
            Email = "revoke-owner@example.test",
            NormalizedEmail = "REVOKE-OWNER@EXAMPLE.TEST",
        });
        context.OrganizationMembers.Add(new OrganizationMember
        {
            OrganizationId = OrganizationId,
            UserId = OwnerUserId,
            Role = OrganizationRole.Owner,
        });
        var token = new WorkerToken
        {
            OrganizationId = OrganizationId,
            Kind = WorkerTokenKind.Registration,
            TokenHash = WorkerTokenCrypto.Hash(WorkerTokenCrypto.Generate(WorkerTokenCrypto.RegistrationPrefix)),
        };
        context.WorkerTokens.Add(token);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return token.Id;
    }

    /// <summary>
    /// B (second review fix): a revoke that races a second revoke - the double-click case, or two
    /// Owners revoking at once - must answer the loser with the pre-check's friendly outcome, not
    /// a <see cref="DbUpdateConcurrencyException"/> (500).
    /// </summary>
    [Fact]
    public async Task A_revoke_racing_another_revoke_is_mapped_to_token_already_revoked()
    {
        var tokenId = await SeedOwnerAndPendingTokenAsync();
        _tenantContext.OrganizationId = OrganizationId;

        var service = new WorkerService(
            new SingleContextFactory(() => CreateContext(new RevocationRaceInterceptor("RevokedAt"))),
            _tenantContext,
            _systemDataScope,
            NullLogger<WorkerService>.Instance);

        var result = await service.RevokeRegistrationTokenAsync(OwnerUserId, tokenId, TestContext.Current.CancellationToken);

        Assert.Equal(WorkerOperationStatus.InvalidInput, result.Status);
        Assert.Equal(WorkerErrorCodes.TokenAlreadyRevoked, result.ErrorCode);

        // The rival's revoke is what the database shows now.
        using var scope = _systemDataScope.BeginScope();
        await using var context = CreateContext();
        var stored = (await context.SystemQueryAsync<WorkerToken>(
            candidate => candidate.Id == tokenId, TestContext.Current.CancellationToken)).Single();
        Assert.NotNull(stored.RevokedAt);
    }

    /// <summary>
    /// B (second review fix): a revoke that races the burn of the same token (an Owner revoking
    /// while a worker registers with it) must answer the loser with the pre-check's friendly
    /// outcome, not a <see cref="DbUpdateConcurrencyException"/> (500).
    /// </summary>
    [Fact]
    public async Task A_revoke_racing_a_burn_is_mapped_to_token_already_consumed()
    {
        var tokenId = await SeedOwnerAndPendingTokenAsync();
        _tenantContext.OrganizationId = OrganizationId;

        var service = new WorkerService(
            new SingleContextFactory(() => CreateContext(new RevocationRaceInterceptor("UsedAt"))),
            _tenantContext,
            _systemDataScope,
            NullLogger<WorkerService>.Instance);

        var result = await service.RevokeRegistrationTokenAsync(OwnerUserId, tokenId, TestContext.Current.CancellationToken);

        Assert.Equal(WorkerOperationStatus.InvalidInput, result.Status);
        Assert.Equal(WorkerErrorCodes.TokenAlreadyConsumed, result.ErrorCode);
    }

    /// <summary>Turns every SaveChanges into the loser's side of the token-burn race.</summary>
    private sealed class BurningRaceInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            throw SimulatedRace();
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            throw SimulatedRace();
        }

        private static DbUpdateConcurrencyException SimulatedRace() =>
            new("The token was burned or revoked by a concurrent registration.");
    }

    /// <summary>
    /// Simulates the losing side of a revocation race faithfully: when the service's SaveChanges
    /// starts, the rival's UPDATE has already landed (executed here as raw SQL on the same row),
    /// so the service's own UPDATE would match no rows - the <see cref="DbUpdateConcurrencyException"/>
    /// the provider raises in a real race. The raw write leaves the database in the exact
    /// post-race state the service's re-read observes.
    /// </summary>
    private sealed class RevocationRaceInterceptor(string column) : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            RaceAsync((ZenkinsDbContext)eventData.Context!, CancellationToken.None).GetAwaiter().GetResult();
            throw SimulatedRace();
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await RaceAsync((ZenkinsDbContext)eventData.Context!, cancellationToken);
            throw SimulatedRace();
        }

        private async Task RaceAsync(ZenkinsDbContext context, CancellationToken cancellationToken)
        {
            var entry = context.ChangeTracker.Entries<WorkerToken>()
                .Single(candidate => candidate.State == EntityState.Modified);

            // The column is a hardcoded test constant, the values are parameter placeholders ({0}, {1}).
            var sql = "UPDATE \"WorkerTokens\" SET \"" + column + "\" = {0} WHERE \"Id\" = {1}";
            await context.Database.ExecuteSqlRawAsync(
                sql,
                [DateTimeOffset.UtcNow, entry.Entity.Id],
                cancellationToken);
        }

        private static DbUpdateConcurrencyException SimulatedRace() =>
            new("The token was revoked or burned by a concurrent request.");
    }

    private sealed class SingleContextFactory(Func<ZenkinsDbContext> create)
        : IDbContextFactory<ZenkinsDbContext>
    {
        public ZenkinsDbContext CreateDbContext() => create();
    }

    public void Dispose()
    {
        // SQLite pools connections; the temp file stays locked until the pool is drained.
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databasePath, _databasePath + "-shm", _databasePath + "-wal" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // A leaked reader must not mask the real failure; the file lives in the OS temp
                // directory and is never committed.
            }
        }
    }
}
