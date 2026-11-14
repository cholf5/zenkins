using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Data.Tenancy;

namespace Zenkins.Tests.Auth;

/// <summary>
/// A real Blazor host for the F-001 authentication tests: the production <c>Program</c> pipeline
/// (static-SSR account pages, cookie authentication, authorization, antiforgery, startup migration)
/// running against a temporary SQLite file, never the repository's <c>App_Data</c> database.
/// <para>
/// Each instance starts empty, so bootstrap tests observe a genuine first run. Construct one per
/// test (not per class): registration mutates the database and the bootstrap outcome must not leak
/// between tests.
/// </para>
/// </summary>
public sealed class AuthTestHost : IDisposable
{
    public const string ValidPassword = "CorrectHorse9!Battery";

    private readonly string _databasePath;
    private readonly string _artifactsPath;
    private readonly WebApplicationFactory<Program> _factory;

    /// <param name="configureTestServices">
    /// Extra DI replacements, applied after the database is pointed at this host's temp file (used
    /// to substitute the bootstrap seam).
    /// </param>
    /// <param name="configureAppConfiguration">Extra configuration sources (used for forwarded-header binding).</param>
    /// <param name="environment">
    /// The ASP.NET Core environment. Development by default (the developer exception page, the startup
    /// migration); F-019's failure-path tests boot the host as Production, because that is the
    /// configuration a published Server ships and the only one that runs the global exception handler.
    /// </param>
    public AuthTestHost(
        Action<IServiceCollection>? configureTestServices = null,
        Action<IConfigurationBuilder>? configureAppConfiguration = null,
        string environment = "Development")
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"zenkins-f001-{Guid.NewGuid():N}.db");

        // F-016: the real Program asserts and creates the artifact store root at startup, so every host that
        // boots it must point the store at its own temp directory. Without this the store defaults to
        // <content root>/App_Data/artifacts - i.e. src/Server/App_Data/artifacts inside the repository, which
        // `**/App_Data/` hides from `git status` and which would therefore accumulate on every full test run.
        _artifactsPath = Path.Combine(Path.GetTempPath(), $"zenkins-f016-store-{Guid.NewGuid():N}");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);

            // Registered before the caller's own sources, so a test that wants a specific root still wins.
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [Zenkins.Server.Artifacts.ArtifactOptions.RootPathConfigurationKey] = _artifactsPath,
                }));

            if (configureAppConfiguration is not null)
            {
                builder.ConfigureAppConfiguration((_, configuration) => configureAppConfiguration(configuration));
            }

            builder.ConfigureTestServices(services =>
            {
                // Program resolves Database:Provider / ConnectionStrings:Default into a singleton;
                // replacing it points every context (and the startup migration) at this temp file.
                services.RemoveAll<ZenkinsDataOptions>();
                services.AddSingleton(new ZenkinsDataOptions(
                    DatabaseProvider.Sqlite,
                    $"Data Source={_databasePath}"));

                configureTestServices?.Invoke(services);
            });
        });
    }

    /// <summary>The running host's service provider (root scope).</summary>
    public IServiceProvider Services => _factory.Services;

    /// <summary>This host's artifact store root (a temp directory, never inside the repository).</summary>
    public string ArtifactsPath => _artifactsPath;

    /// <summary>
    /// The in-memory <see cref="TestServer"/>: F-008's hub tests build SignalR connections whose
    /// requests must reach this process (no real port exists), so they route the client through
    /// this handler.
    /// </summary>
    public TestServer Server => _factory.Server;

    /// <summary>A new DI scope, for code that needs two scopes concurrently (the bootstrap race test).</summary>
    public AsyncServiceScope CreateScope() => _factory.Services.CreateAsyncScope();

    /// <summary>Absolute path of this host's temporary SQLite file (never inside the repository).</summary>
    public string DatabasePath => _databasePath;

    /// <summary>A client with a private cookie jar that does not follow redirects (assert them instead).</summary>
    public HttpClient CreateClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = new Uri("http://localhost"),
    });

    /// <summary>A client whose requests are HTTPS, for asserting <c>Secure</c> on the auth cookie.</summary>
    public HttpClient CreateHttpsClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = new Uri("https://localhost"),
    });

    /// <summary>Registers an account through the real static-SSR form (token, post, redirect).</summary>
    public static async Task<HttpResponseMessage> RegisterAsync(
        HttpClient client,
        string email,
        string password = ValidPassword,
        string? confirmPassword = null)
    {
        confirmPassword ??= password;
        var registerHtml = await GetStringAsync(client, "/account/register");
        var token = ExtractAntiforgeryToken(registerHtml);

        return await PostFormAsync(client, "/account/register", new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["Input.ConfirmPassword"] = confirmPassword,
            ["__RequestVerificationToken"] = token,
            ["_handler"] = "register",
        });
    }

    /// <summary>Signs in through the real static-SSR form, preserving <paramref name="returnUrl"/> in the query.</summary>
    public static async Task<HttpResponseMessage> LoginAsync(
        HttpClient client,
        string email,
        string password = ValidPassword,
        string? returnUrl = null)
    {
        var url = returnUrl is null
            ? "/account/login"
            : $"/account/login?ReturnUrl={Uri.EscapeDataString(returnUrl)}";
        var loginHtml = await GetStringAsync(client, url);
        var token = ExtractAntiforgeryToken(loginHtml);

        return await PostFormAsync(client, url, new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["__RequestVerificationToken"] = token,
            ["_handler"] = "login",
        });
    }

    public static string ExtractAntiforgeryToken(string html)
    {
        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new InvalidOperationException("The page did not render an antiforgery token.");
        }

        return match.Groups["token"].Value;
    }

    public static async Task<string> GetStringAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client,
        string url,
        IReadOnlyDictionary<string, string> fields)
    {
        var content = new FormUrlEncodedContent(fields);
        return client.PostAsync(url, content);
    }

    /// <summary>The <c>Set-Cookie</c> header for the authentication cookie, or <see langword="null"/>.</summary>
    public static string? FindAuthCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        return values.FirstOrDefault(value =>
            value.StartsWith("Zenkins.Auth=", StringComparison.Ordinal));
    }

    /// <summary>Reads the bootstrap-relevant rows through the product's data layer.</summary>
    public async Task<AuthSnapshot> ReadSnapshotAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var systemDataScope = scope.ServiceProvider.GetRequiredService<ISystemDataScope>();
        await using var context = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ZenkinsDbContext>>()
            .CreateDbContextAsync();

        using var systemScope = systemDataScope.BeginScope();
        var memberships = await context.SystemQueryAsync<OrganizationMember>(_ => true);

        return new AuthSnapshot(
            Users: await context.Users.CountAsync(),
            Organizations: await context.Organizations.CountAsync(),
            Memberships: memberships.Count,
            OwnerMemberships: memberships.Count(member => member.Role == OrganizationRole.Owner),
            BootstrapMarkers: await context.BootstrapMarkers.CountAsync(),
            UserIds: await context.Users.Select(user => user.Id).ToListAsync(),
            MembershipUserIds: memberships.Select(member => member.UserId).ToList());
    }

    /// <summary>Runs <paramref name="action"/> in its own DI scope (one request/operation).</summary>
    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public void Dispose()
    {
        _factory.Dispose();

        // SQLite pools connections; on Windows the file stays locked until the pool is drained.
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
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // The artifact store this host created (F-016): a temp directory, removed with the host.
        try
        {
            if (Directory.Exists(_artifactsPath))
            {
                Directory.Delete(_artifactsPath, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Bootstrap-relevant row counts, read across all tenants.</summary>
public sealed record AuthSnapshot(
    int Users,
    int Organizations,
    int Memberships,
    int OwnerMemberships,
    int BootstrapMarkers,
    IReadOnlyList<string> UserIds,
    IReadOnlyList<string> MembershipUserIds);
