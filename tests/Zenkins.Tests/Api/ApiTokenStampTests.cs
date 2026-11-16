using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Zenkins.Server.Api;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Tests.Organizations;

namespace Zenkins.Tests.Api;

/// <summary>
/// Card Q-015's two leftovers with a real net: the API token's last-used stamp is best effort in every
/// failure mode and bounded in time, and the token list's Owner check is exercised directly (the page
/// short-circuits before the service, so no HTTP test ever reaches it).
/// </summary>
[Trait("Category", "Api")]
public sealed class ApiTokenStampTests(ITestOutputHelper output)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    /// <summary>
    /// The stamp cannot fail a request, and it cannot stall one: with the SQLite database's write lock
    /// held by someone else, an authenticated read still answers 200 - after the stamp's own three-second
    /// command timeout, not the provider's default thirty (the number this card measured before the fix,
    /// recorded on the card).
    /// </summary>
    [Fact]
    public async Task A_locked_database_costs_the_stamp_seconds_and_never_fails_the_request()
    {
        var logs = new RecordingLogProvider();
        using var host = await ApiV1TestHost.CreateAsync(
            configureTestServices: services => services.AddSingleton<ILoggerProvider>(logs));
        var (_, owner) = await host.CreateProjectAsync();
        var token = await host.MintTokenPlaintextAsync(owner, "stamp-under-lock");
        using var api = host.CreateApiClient(token);

        // BEGIN IMMEDIATE takes SQLite's RESERVED lock: readers proceed, the stamp's write cannot.
        await using var holder = new SqliteConnection($"Data Source={host.Inner.Inner.Inner.Host.DatabasePath}");
        await holder.OpenAsync(CancellationToken);
        await using var held = holder.BeginTransaction(deferred: false);

        var stopwatch = Stopwatch.StartNew();
        using var response = await ApiV1TestHost.GetAsync(api, "/api/v1/projects");
        stopwatch.Stop();
        await held.RollbackAsync(CancellationToken);
        output.WriteLine($"authenticated read under a held write lock: {stopwatch.Elapsed.TotalSeconds:F1}s");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The stamp's own timeout is 3 s; the bound leaves room for a loaded machine but not for the
        // 30 s default this card removed.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"the authenticated read waited {stopwatch.Elapsed.TotalSeconds:F1}s on the last-used stamp - "
            + "the stamp's command timeout is not doing its job (the pre-fix measurement was the provider's 30 s).");

        // Logged once, as a warning, and never as an error that would suggest the request failed.
        var stampWarnings = logs.Entries
            .Where(entry => entry.Message.Contains("last-used", StringComparison.Ordinal))
            .ToArray();
        var warning = Assert.Single(stampWarnings);
        Assert.Equal(LogLevel.Warning, warning.Level);

        // The mark is genuinely best effort: the token still reads as never used, and a later request
        // (no lock) writes it.
        var tokenRow = Assert.Single(await host.ReadApiTokensAsync());
        Assert.Null(tokenRow.LastUsedAt);

        using var later = await ApiV1TestHost.GetAsync(api, "/api/v1/projects");
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
        tokenRow = Assert.Single(await host.ReadApiTokensAsync());
        Assert.NotNull(tokenRow.LastUsedAt);
    }

    /// <summary>
    /// <see cref="ApiTokenService.GetTokensAsync"/> is Owner-only in the <b>service</b>, not only on the
    /// page: a Member gets an empty list (no rows, no error), an Owner gets the rows, and a request with
    /// no organization selected gets an empty list. Deleting the service's check turns the Member
    /// assertion red.
    /// </summary>
    [Fact]
    public async Task The_token_list_is_owner_only_in_the_service_a_member_gets_an_empty_list()
    {
        using var host = await ApiV1TestHost.CreateAsync();
        var (_, owner) = await host.CreateProjectAsync();
        await host.MintTokenPlaintextAsync(owner, "listed");

        var organizationId = await host.Inner.OrganizationIdAsync();
        var ownerUserId = (await host.Inner.Inner.Inner.ReadUserAsync(OrganizationTestHost.OwnerEmail)).Id;

        // A second account, a Member of the same organization (seeded directly: roles are an org form,
        // and this fact is about the service's own check, not about the form that grants the role).
        using var memberClient = await host.Inner.Inner.Inner.RegisterUserAsync("bob@example.test");
        var memberUserId = (await host.Inner.Inner.Inner.ReadUserAsync("bob@example.test")).Id;
        await host.Inner.Inner.Inner.Host.InScopeAsync(async services =>
        {
            var systemDataScope = services.GetRequiredService<ISystemDataScope>();
            await using var context = await services
                .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
                .CreateDbContextAsync(CancellationToken);
            using var scope = systemDataScope.BeginScope();
            context.OrganizationMembers.Add(new OrganizationMember
            {
                OrganizationId = organizationId,
                UserId = memberUserId,
                Role = OrganizationRole.Member,
            });
            await context.SaveChangesAsync(CancellationToken);
            return true;
        });

        var ownerRows = await ReadTokensAsync(host, organizationId, ownerUserId);
        Assert.Single(ownerRows);
        Assert.Equal("listed", ownerRows[0].Name);

        var memberRows = await ReadTokensAsync(host, organizationId, memberUserId);
        Assert.Empty(memberRows);

        var noTenantRows = await ReadTokensAsync(host, organizationId: null, ownerUserId);
        Assert.Empty(noTenantRows);
    }

    /// <summary>
    /// Calls the service the way the page does, inside one DI scope whose <see cref="TenantContext"/>
    /// carries the tenant a request would have selected.
    /// </summary>
    private static Task<IReadOnlyList<ApiTokenView>> ReadTokensAsync(
        ApiV1TestHost host,
        Guid? organizationId,
        string userId) =>
        host.Inner.Inner.Inner.Host.InScopeAsync(async services =>
        {
            services.GetRequiredService<TenantContext>().OrganizationId = organizationId;
            return await services.GetRequiredService<ApiTokenService>().GetTokensAsync(userId, CancellationToken);
        });
}

/// <summary>
/// A logger provider that keeps what the host logged, so a fact can assert on the count and level of a
/// warning a best-effort path emits (nothing else in the suite reads logs).
/// </summary>
internal sealed class RecordingLogProvider : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Entries);

    public void Dispose()
    {
    }

    private sealed class RecordingLogger(
        string category,
        ConcurrentQueue<(LogLevel Level, string Message)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _ = category;
            entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
