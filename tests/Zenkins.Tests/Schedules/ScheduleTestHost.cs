using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;
using Zenkins.Server.Schedules;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Runs;

namespace Zenkins.Tests.Schedules;

/// <summary>
/// Card F-024's HTTP test host: <see cref="RunTestHost"/> (the real Server pipeline over a temp SQLite
/// database, a real form trigger and the real worker API) plus the schedule side - the settings forms, a
/// direct seed for a due time, and one evaluator tick through the background service's own seam.
/// <para>
/// <b>Why the background timer is pushed far out.</b> The host boots the production composition root, which
/// registers <see cref="ScheduleSweeper"/>; it ticks on its own every <c>Schedules:TickSeconds</c>. A test that
/// seeds "a due time arrived" and then waits would race that timer, so the timer is set to an hour and every
/// test drives <see cref="SweepAsync"/> explicitly - a tick, not a sleep. (The startup tick still runs once,
/// before any test has a schedule, so it is a no-op.)
/// </para>
/// </summary>
public sealed class ScheduleTestHost : IDisposable
{
    private ScheduleTestHost(RunTestHost inner) => Inner = inner;

    public RunTestHost Inner { get; }

    /// <param name="clock">
    /// The schedule service's own clock, so "a due time arrived" is exact instead of depending on where in the
    /// minute the test happens to run - and so a fact that needs two instants can move it. Everything else keeps
    /// the real clock (the audit stamps the context writes, the run rows, the worker API), which is what makes
    /// this a fixture rather than a simulation.
    /// </param>
    public static async Task<ScheduleTestHost> CreateAsync(
        Action<IConfigurationBuilder>? configureAppConfiguration = null,
        Action<IServiceCollection>? configureTestServices = null,
        TestClock? clock = null)
    {
        var inner = await RunTestHost.CreateAsync(
            builder =>
            {
                configureAppConfiguration?.Invoke(builder);
                builder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ScheduleOptions.TickSecondsConfigurationKey] = "3600",
                });
            },
            services =>
            {
                configureTestServices?.Invoke(services);
                if (clock is { } supplied)
                {
                    // Last registration wins for a single resolve, so this is the clock ScheduleService reads.
                    services.AddSingleton<TimeProvider>(supplied);
                }
            });
        return new(inner);
    }

    public HttpClient CreateClient() => Inner.Host.CreateClient();

    public Task<Guid> OrganizationIdAsync() => Inner.OrganizationIdAsync();

    /// <summary>Creates a Pipeline project through the real form; returns its id and an Owner client.</summary>
    public Task<(Guid ProjectId, HttpClient Owner)> CreateProjectAsync(string name) =>
        Inner.CreateProjectAsync(name);

    /// <summary>
    /// Seeds one schedule directly (the due-time fixtures are about the evaluator, not the form): its
    /// watermark is <paramref name="evaluatedAgo"/> in the past, which is what makes a due time "arrive".
    /// </summary>
    public Task<Guid> SeedScheduleAsync(
        Guid projectId,
        string cron = "* * * * *",
        string timeZone = "UTC",
        bool enabled = true,
        bool queueAnyway = false,
        TimeSpan? evaluatedAgo = null,
        DateTimeOffset? evaluatedAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        var watermark = evaluatedAt ?? now - (evaluatedAgo ?? TimeSpan.FromSeconds(10));

        return Inner.InSystemScopeAsync(async context =>
        {
            var schedule = new ProjectSchedule
            {
                OrganizationId = (await context.SystemQueryAsync<Project>(project => project.Id == projectId))
                    .Single()
                    .OrganizationId,
                ProjectId = projectId,
                Cron = cron,
                TimeZone = timeZone,
                Enabled = enabled,
                QueueAnyway = queueAnyway,
                CreatedByUserId = "seeded-by-the-test",
                // Created well before the wedge so the watermark (not the creation instant) is what decides.
                CreatedAt = now - TimeSpan.FromDays(1),
                LastEvaluatedAt = watermark,
                UpdatedAt = now,
            };
            context.ProjectSchedules.Add(schedule);
            await context.SaveChangesAsync();
            return schedule.Id;
        });
    }

    /// <summary>How many whole-minute due times a per-minute cron has in <c>(watermark, now]</c>.</summary>
    public static int MinutesBetween(DateTimeOffset watermark, DateTimeOffset now) =>
        (int)(now.ToUnixTimeSeconds() / 60 - watermark.ToUnixTimeSeconds() / 60);

    /// <summary>One evaluator tick through the sweeper's own test seam (never a sleep).</summary>
    public Task<ScheduleTickResult> SweepAsync() =>
        Inner.Host.InScopeAsync(async services =>
        {
            var sweeper = services.GetServices<IHostedService>().OfType<ScheduleSweeper>().Single();
            return await sweeper.SweepOnceAsync();
        });

    /// <summary>Reads one schedule row across tenants.</summary>
    public Task<ProjectSchedule> ReadScheduleAsync(Guid scheduleId) =>
        Inner.InSystemScopeAsync(async context =>
            (await context.SystemQueryAsync<ProjectSchedule>(schedule => schedule.Id == scheduleId)).Single());

    /// <summary>Every due-time row of one schedule, newest first (ordered in memory: SQLite cannot order
    /// DateTimeOffset columns in SQL).</summary>
    public Task<IReadOnlyList<ScheduleOccurrence>> ReadOccurrencesAsync(Guid scheduleId) =>
        Inner.InSystemScopeAsync<IReadOnlyList<ScheduleOccurrence>>(async context =>
            [.. (await context.SystemQueryAsync<ScheduleOccurrence>(
                    occurrence => occurrence.ScheduleId == scheduleId))
                .OrderByDescending(occurrence => occurrence.DueAt)]);

    /// <summary>Reads the project's runs (the assertion side of "nothing was queued").</summary>
    public Task<IReadOnlyList<BuildRun>> ReadRunsAsync(Guid organizationId) => Inner.ReadRunsAsync(organizationId);

    public Task<BuildRun> ReadRunAsync(Guid runId) => Inner.ReadRunAsync(runId);

    // ------------------------------------------------------------------
    // The settings tab's forms
    // ------------------------------------------------------------------

    /// <summary>Posts the Schedules card's create form, optionally as the Preview button.</summary>
    public static async Task<HttpResponseMessage> CreateScheduleAsync(
        HttpClient client,
        Guid projectId,
        string? cron,
        string? timeZone,
        bool enabled = true,
        bool queueAnyway = false,
        bool preview = false)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Cron"] = cron ?? string.Empty,
            ["TimeZone"] = timeZone ?? string.Empty,
        };

        if (enabled)
        {
            fields["Enabled"] = "true";
        }

        if (queueAnyway)
        {
            fields["QueueAnyway"] = "true";
        }

        if (preview)
        {
            fields["Action"] = "preview";
        }

        return await OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{projectId}/settings",
            $"/projects/{projectId}/schedules/create",
            fields);
    }

    /// <summary>Flips one of a schedule's two switches through the card's form.</summary>
    public static Task<HttpResponseMessage> ToggleScheduleAsync(
        HttpClient client,
        Guid projectId,
        Guid scheduleId,
        string flag,
        bool value) =>
        OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{projectId}/settings",
            $"/projects/{projectId}/schedules/{scheduleId}/toggle",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Flag"] = flag,
                ["Value"] = value ? "true" : "false",
            });

    public static Task<HttpResponseMessage> DeleteScheduleAsync(
        HttpClient client,
        Guid projectId,
        Guid scheduleId) =>
        OrganizationTestHost.PostFormAsync(
            client,
            $"/projects/{projectId}/settings",
            $"/projects/{projectId}/schedules/{scheduleId}/delete",
            new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>The settings page rendered (the page half of "the skip is the last outcome").</summary>
    public static Task<string> ReadSettingsPageAsync(HttpClient client, Guid projectId, string query = "") =>
        AuthTestHost.GetStringAsync(client, $"/projects/{projectId}/settings{query}");

    /// <summary>Asserts a PRG redirect and returns its location.</summary>
    public static string Redirect(HttpResponseMessage response)
    {
        Assert.True(
            (int)response.StatusCode is 302 or 303,
            $"Expected a redirect, got {(int)response.StatusCode}.");
        return response.Headers.Location!.OriginalString;
    }

    /// <summary>Reads one project row across tenants (the seed side of the fixtures).</summary>
    public Task<Project> ReadProjectAsync(Guid projectId) => Inner.ReadProjectAsync(projectId);

    /// <summary>Seeds the project's parameter definitions through the real Parameters form.</summary>
    public async Task SaveStringParameterAsync(HttpClient owner, Guid projectId, string name, string defaultValue)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new object[]
        {
            new
            {
                name,
                type = "string",
                defaultValue,
                choicesText = string.Empty,
                description = string.Empty,
            },
        });
        var response = await OrganizationTestHost.PostFormAsync(
            owner,
            $"/projects/{projectId}/parameters",
            $"/projects/{projectId}/parameters",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ParametersJson"] = json });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    /// <summary>
    /// Seeds a Freestyle project with one saved step (the card's "both kinds support schedules"). The steps
    /// editor's own JSON is the published shape, so the seed goes through it rather than inventing a spec.
    /// </summary>
    public Task<Guid> SeedFreestyleProjectAsync(string name) =>
        Inner.InSystemScopeAsync(async context =>
        {
            var project = new Project
            {
                OrganizationId = await OrganizationIdAsync(),
                Name = name,
                Kind = Zenkins.Shared.Projects.ProjectKinds.Freestyle,
                RepositoryUrl = null,
                DefaultBranch = null,
                PipelinePath = null,
                FreestyleSpecJson = "{\"name\":\"freestyle\",\"steps\":[{\"name\":\"say-hi\","
                    + "\"script\":\"echo hi\",\"continueOnError\":false}]}",
            };
            context.Projects.Add(project);
            await context.SaveChangesAsync();
            return project.Id;
        });

    /// <summary>One system-scope callback for fixtures that need to write a row the product normally owns.</summary>
    public Task<T> InSystemScopeAsync<T>(Func<ZenkinsDbContext, Task<T>> action) => Inner.InSystemScopeAsync(action);

    /// <summary>Reads the run's parameter snapshot (F-021's proof that a scheduled run carries the defaults).</summary>
    public static IReadOnlyDictionary<string, string> ReadParameters(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return new Dictionary<string, string>();
        }

        using var document = System.Text.Json.JsonDocument.Parse(parametersJson);
        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty);
    }

    public void Dispose() => Inner.Dispose();
}

/// <summary>
/// The schedule service's clock, moved by the test: a fixed instant for the deterministic facts, and
/// <see cref="Advance"/> for the one that needs a second due time to arrive.
/// </summary>
public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>Moves the clock forward - the honest way to reach the next due time without waiting for it.</summary>
    public void Advance(TimeSpan by) => Now += by;
}
