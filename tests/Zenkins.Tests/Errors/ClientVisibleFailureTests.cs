using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;
using Zenkins.Server.Secrets;
using Zenkins.Tests.Auth;
using Zenkins.Tests.Organizations;
using Zenkins.Tests.Webhooks;

namespace Zenkins.Tests.Errors;

/// <summary>
/// Card F-019's second requirement, verified rather than assumed: when a request fails unexpectedly,
/// the bytes a client receives carry no stack trace, no exception type name and no source path - and
/// the page the client gets is the product's own error page, not an empty response. The detail exists
/// only in the server log.
/// <para>
/// <b>This is a Production property.</b> The host boots as <c>Production</c> on purpose: <c>Program</c>
/// registers <c>UseExceptionHandler</c> only off Development, so a Development run deliberately answers
/// with the developer exception page - which does show the stack and the absolute source path. A test
/// that booted Development would fail here, and that is expected, not a bug: the shipped configuration
/// is the one a client can reach.
/// </para>
/// <para>
/// The fault is injected without touching a product route: an <see cref="IStartupFilter"/> appends a
/// throwing middleware at the <em>end</em> of the pipeline (`next(app)`, then the fault). Requests that
/// matched a real endpoint have already been handled by then, so only an unrouted path reaches it - and
/// because the middleware sits inside <c>UseExceptionHandler</c>, the exception travels through the
/// real handler and the real error page. Reversing that order would throw before the handler is
/// installed and produce a bare 500, which the page assertion below rejects.
/// </para>
/// <para>
/// The second test makes the same point on a form endpoint itself (the card's AC names the forms):
/// the handler's dependency is unresolvable, so the fault happens inside the real request, after
/// antiforgery and authentication, exactly where a genuine defect would happen.
/// </para>
/// <para>
/// <b>The two cases are not interchangeable - keep the method split.</b> This one issues a GET, so
/// <c>/error</c> is re-executed as GET. <c>UseExceptionHandler</c> replays the <em>original method</em>,
/// so only the form case (a POST) pins that <c>/error</c> also accepts POST; if it were GET-only, a form
/// failure would become an empty 405 and its assertion would fail. Do not turn the form case into a
/// GET-shaped fault without moving that guarantee somewhere else.
/// </para>
/// </summary>
[Trait("Category", "Errors")]
public sealed class ClientVisibleFailureTests
{
    /// <summary>The unrouted path the fault middleware answers.</summary>
    private const string FaultPath = "/f019-fault-probe";

    /// <summary>A message no client may ever see: it is the marker the log assertion looks for.</summary>
    private const string FaultMessage = "f019-fault-probe 7d1c: the pipeline fault probe fired";

    /// <summary>The error page's fixed copy: a failure body has to actually be that page.</summary>
    private const string ErrorPageCopy = "Something went wrong";

    private const string OwnerEmail = "leak-probe@example.test";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    /// <summary>
    /// Everything a failure body must not contain (AC-3): a stack frame, a source line, an exception
    /// type name, a framework namespace, an absolute path.
    /// </summary>
    private static readonly (string Name, Regex Pattern)[] LeakMarkers =
    [
        ("stack frame", new Regex(@"\bat\s+[A-Za-z_][\w.<>`+]+\s*[.(]", RegexOptions.CultureInvariant)),
        ("source line", new Regex(@"\.cs:line\s+\d+", RegexOptions.CultureInvariant)),
        ("exception type", new Regex(@"\b\w+Exception\b", RegexOptions.CultureInvariant)),
        ("framework namespace", new Regex(@"Microsoft\.(AspNetCore|Extensions|EntityFrameworkCore)\.", RegexOptions.CultureInvariant)),
        ("absolute path", new Regex(@"[A-Za-z]:[\\/]|[/](?:home|Users|tmp|private|var|opt|usr|mnt|root)[/]|src[\\/]Server[\\/]", RegexOptions.CultureInvariant)),
    ];

    /// <summary>
    /// A GET, on purpose: the POST re-execution of <c>/error</c> is pinned by the form case below, which
    /// is the only test here that exercises it (see the class remarks).
    /// </summary>
    [Fact]
    public async Task An_unhandled_fault_answers_with_the_generic_page_and_no_stack()
    {
        var logs = new CapturingLoggerProvider();
        var keysDirectory = Path.Combine(Path.GetTempPath(), $"zenkins-f019-keys-{Guid.NewGuid():N}");

        try
        {
            using var host = CreateProductionHost(
                logs,
                keysDirectory,
                configureTestServices: services =>
                    services.AddSingleton<IStartupFilter>(new ThrowingPipelineFilter(FaultPath, FaultMessage)));

            using var client = host.CreateClient();

            using var response = await client.GetAsync(FaultPath, CancellationToken);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Contains(
                "text/html",
                response.Content.Headers.ContentType?.ToString() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);

            var body = await response.Content.ReadAsStringAsync(CancellationToken);
            Assert.Contains(ErrorPageCopy, body, StringComparison.Ordinal);
            AssertNoLeak(body);

            // Not suppressed - relocated: the message and the exception live in the server log.
            var serverLog = string.Join("\n", logs.Lines);
            Assert.Contains(FaultMessage, serverLog, StringComparison.Ordinal);
            Assert.Contains("InvalidOperationException", serverLog, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(keysDirectory);
        }
    }

    /// <summary>
    /// The same on a form endpoint: a valid token, a valid body, and a handler whose dependency cannot
    /// be resolved. The client still gets the generic page, and the form's own data is not echoed.
    /// <para>
    /// This is the POST case, and the only one that proves <c>/error</c> accepts POST: the exception
    /// handler replays the original method, so a GET-only error page would answer 405 with no body
    /// here. Keep it a POST.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_form_handlers_fault_answers_with_the_generic_page_and_no_stack()
    {
        var logs = new CapturingLoggerProvider();
        var keysDirectory = Path.Combine(Path.GetTempPath(), $"zenkins-f019-keys-{Guid.NewGuid():N}");

        try
        {
            using var host = CreateProductionHost(
                logs,
                keysDirectory,
                configureTestServices: services =>
                {
                    // The fault: resolving the protector throws while the endpoint's parameters are bound.
                    services.RemoveAll<SecretProtector>();
                    services.AddScoped<SecretProtector>(_ => throw new InvalidOperationException(FaultMessage));
                });

            using var client = host.CreateClient();

            using (var registration = await AuthTestHost.RegisterAsync(client, OwnerEmail))
            {
                Assert.True(
                    (int)registration.StatusCode is 302 or 303,
                    $"Registration failed with {(int)registration.StatusCode}.");
            }

            var page = await AuthTestHost.GetStringAsync(client, "/");
            var token = OrganizationTestHost.ExtractAntiforgeryMetaToken(page);

            using var response = await client.PostAsync(
                "/secrets/create",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["Name"] = "LEAK_PROBE",
                    ["Value"] = "leak-probe-value",
                    ["__RequestVerificationToken"] = token,
                }),
                CancellationToken);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(CancellationToken);
            Assert.Contains(ErrorPageCopy, body, StringComparison.Ordinal);
            AssertNoLeak(body);

            // The submitted secret's value never travels to the client on the failure path either.
            Assert.DoesNotContain("leak-probe-value", body, StringComparison.Ordinal);

            var serverLog = string.Join("\n", logs.Lines);
            Assert.Contains(FaultMessage, serverLog, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(keysDirectory);
        }
    }

    /// <summary>
    /// The Production-shaped host both tests need: the shipped environment, the schema applied (a
    /// Production run does not migrate on startup), a temporary key ring and the package's log sink.
    /// </summary>
    private static AuthTestHost CreateProductionHost(
        CapturingLoggerProvider logs,
        string keysDirectory,
        Action<IServiceCollection> configureTestServices) =>
        new(
            configureTestServices: services =>
            {
                services.AddSingleton<ILoggerProvider>(logs);
                configureTestServices(services);
            },
            configureAppConfiguration: configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [SecretDataProtection.KeysDirectoryConfigurationKey] = keysDirectory,
                    ["Database:ApplyMigrationsOnStartup"] = "true",
                }),
            environment: "Production");

    /// <summary>Fails when a body carries a stack, an internal type name or a source path.</summary>
    private static void AssertNoLeak(string body) =>
        Assert.All(
            LeakMarkers,
            marker => Assert.False(
                marker.Pattern.IsMatch(body),
                $"The failure body leaked a {marker.Name}: {marker.Pattern}"));

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked read handle must not mask the real assertion; the directory is in the temp folder.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Appends a terminal, throwing middleware after everything <c>Program</c> registered - including
    /// the endpoint middleware - and only answers <paramref name="faultPath"/>, so routed requests are
    /// unaffected. It is added by an <see cref="IStartupFilter"/> because the request pipeline is built
    /// from those filters: nothing in the product's own startup changes.
    /// </summary>
    private sealed class ThrowingPipelineFilter(string faultPath, string message) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);

            app.Run(context => context.Request.Path.Equals(faultPath, StringComparison.Ordinal)
                ? throw new InvalidOperationException(message)
                : Task.CompletedTask);
        };
    }
}
