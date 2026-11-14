using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Zenkins.Worker;

/// <summary>
/// Entry point for the Zenkins build worker: a plain console process that must never depend on
/// ASP.NET Core (it is published self-contained/trimmed). It dials the Server over HTTP
/// (card F-005), registers once with an Owner-minted registration token, persists the per-worker
/// token locally, heartbeats on an interval, retries failures with backoff, and shuts down
/// gracefully on SIGINT/SIGTERM.
/// <para>
/// Exit codes: <c>0</c> graceful shutdown, <see cref="WorkerRunner.ConfigurationExitCode"/> (2)
/// invalid configuration or unusable persisted state, <see cref="WorkerRunner.RegistrationRejectedExitCode"/>
/// (3) registration permanently rejected - the server gave a definitive 4xx answer to the register
/// call, so the process parsed and started fine but cannot ever register as configured. Any of the
/// non-zero paths print exactly one line to stderr.
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>
    /// How long the SIGTERM signal thread waits for the graceful path before exiting anyway. The
    /// best-effort final heartbeat timeout (<see cref="WorkerRunner"/>) stays well inside it.
    /// </summary>
    private static readonly TimeSpan SignalGracePeriod = TimeSpan.FromSeconds(5);

    public static async Task<int> Main()
    {
        var configurationResult = WorkerConfiguration.FromEnvironment(ReadEnvironment());
        if (configurationResult.Error is { } error)
        {
            Console.Error.WriteLine("Zenkins Worker: " + error);
            return WorkerRunner.ConfigurationExitCode;
        }

        var configuration = configurationResult.Configuration!;

        using var shutdownSource = new CancellationTokenSource();
        using var shutdownComplete = new ManualResetEventSlim(false);
        using var sigint = RegisterSignal(PosixSignal.SIGINT, shutdownSource, gracefulStopCompleted: null);
        using var sigterm = RegisterSignal(PosixSignal.SIGTERM, shutdownSource, shutdownComplete);

        // Belt and braces for SIGINT (see RegisterSignal's platform notes): the classic Ctrl+C hook
        // alongside PosixSignalRegistration. Both paths only cancel, and double-cancel is harmless.
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            shutdownSource.Cancel();
        };

        using var httpClient = new HttpClient(new SocketsHttpHandler
        {
            // A closed port or dead network must fail fast, not sit in the OS connect timeout.
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = System.Net.DecompressionMethods.None,
        })
        {
            BaseAddress = configuration.ServerUrl,
        };

        var runner = new WorkerRunner(
            configuration,
            new WorkerApiClient(httpClient),
            new WorkerTokenStore(
                configuration.DataDirectory,
                configuration.ServerUrl,
                configuration.WorkerName),
            new WorkDirectoryService(
                // The run directories' root: a relative value (the default "work", or a relative
                // ZENKINS_WORK_ROOT) resolves against AppContext.BaseDirectory, not the current
                // directory - the I-003 path discipline; absolute values pass through unchanged.
                configuration.ResolveWorkRoot()),
            BackoffScheduler.Default(),
            Console.Out,
            Console.Error);

        try
        {
            return await runner.RunAsync(shutdownSource.Token);
        }
        catch (WorkerStartupException startup)
        {
            Console.Error.WriteLine("Zenkins Worker: " + startup.Message);
            // 2 for configuration errors, 3 for a permanently rejected registration (C, second
            // review fix) - the distinction lives on the exception, both paths print one line.
            return startup.ExitCode;
        }
        finally
        {
            // The SIGTERM signal thread (see RegisterSignal) waits on this before exiting cleanly.
            shutdownComplete.Set();
        }
    }

    /// <summary>The process environment as a plain dictionary (case-sensitive keys).</summary>
    private static IReadOnlyDictionary<string, string?> ReadEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = entry.Value as string;
        }

        return environment;
    }

    /// <summary>
    /// Wires the shutdown signals to the shutdown token. <see cref="PosixSignalRegistration"/> is
    /// the documented API for both signals (on Windows via the console control handler); registering
    /// a handler is what disables the abrupt default termination.
    /// <para>
    /// <b>Platform notes (observed on .NET 10 / macOS, handled here rather than assumed):</b>
    /// after the SIGTERM handler returns, the runtime re-raises SIGTERM with the default disposition
    /// (a print-only handler still died with exit 143). The SIGTERM handler therefore cancels the
    /// shutdown token, waits a bounded time for <c>Main</c> to finish the graceful path (final
    /// heartbeat included) and exits with code 0 from the signal thread - identical on platforms
    /// where default termination is genuinely prevented, because there the process has already
    /// exited 0 through <c>Main</c> before the wait ends. SIGINT, on the other hand, never fires the
    /// registered callback on macOS when stdin is redirected (a human terminal is the real case), so
    /// <see cref="Console.CancelKeyPress"/> is registered as well; both paths only cancel, and the
    /// process exits 0 through <c>Main</c>.
    /// </para>
    /// </summary>
    private static PosixSignalRegistration? RegisterSignal(
        PosixSignal signal,
        CancellationTokenSource shutdownSource,
        ManualResetEventSlim? gracefulStopCompleted)
    {
        try
        {
            return PosixSignalRegistration.Create(signal, _ => HandleSignal(shutdownSource, gracefulStopCompleted));
        }
        catch (PlatformNotSupportedException)
        {
            if (signal == PosixSignal.SIGINT)
            {
                // Fallback where PosixSignalRegistration is unavailable: Ctrl+C only.
                Console.CancelKeyPress += (_, args) =>
                {
                    args.Cancel = true;
                    shutdownSource.Cancel();
                };
            }

            return null;
        }
    }

    private static void HandleSignal(
        CancellationTokenSource shutdownSource,
        ManualResetEventSlim? gracefulStopCompleted)
    {
        shutdownSource.Cancel();
        if (gracefulStopCompleted is null)
        {
            return;
        }

        gracefulStopCompleted.Wait(SignalGracePeriod);
        Environment.Exit(0);
    }
}
