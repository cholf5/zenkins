namespace Zenkins.Tests.TestAssets.ArgvEcho;

/// <summary>
/// A tiny cross-platform console tool the WorkerRuntime tests use to prove process behaviour from
/// inside a real child process (card F-007). It is referenced by the test project (build-only, no
/// output copying) so building the tests builds this too; tests locate the DLL under its
/// <c>bin</c> directory and run it through <c>dotnet exec</c>.
/// <para>
/// Modes (first argument selects; anything else is data):
/// <list type="bullet">
/// <item><c>echo</c> — prints every remaining argument raw, one per line, to stdout; exit 0.
/// The argv-fidelity probe: arguments with spaces, quotes, <c>$</c>, <c>;</c>, <c>&amp;&amp;</c>
/// or a leading dash come back verbatim only when no shell or re-quoting touched them.</item>
/// <item><c>echo-each &lt;ms&gt;</c> — prints each remaining argument, sleeping ms between them.
/// The slow-output probe: lets a test observe the stream file growing before the process exits.</item>
/// <item><c>probe &lt;file&gt; &lt;text&gt;</c> — writes text into the file (cwd-relative) and
/// prints <c>probe-written</c>. The working-directory probe for per-run isolation.</item>
/// <item><c>append &lt;file&gt; &lt;text&gt;</c> — appends a line to the file (cwd-relative) and
/// prints <c>appended</c>. The F-014 step-order marker: one line per executed step.</item>
/// <item><c>write-slow &lt;ms&gt; &lt;text&gt;</c> — prints <c>stream-first</c> to stdout, sleeps
/// ms, prints text to stderr, sleeps ms again, prints <c>stream-second</c> to stdout, exits 0.
/// The streaming-before-exit probe (stdout and stderr, ordered per stream).</item>
/// <item><c>stream &lt;ms&gt;</c> — prints <c>stream-first</c>, sleeps ms, prints
/// <c>stream-second</c>, exits 0. A long-lived but well-behaved child.</item>
/// <item><c>spawn &lt;ms&gt;</c> — starts a grandchild (<c>stream &lt;ms&gt;</c> of this same
/// DLL), prints <c>grandchild-pid &lt;pid&gt;</c>, sleeps ms, exits 0. The kill-tree probe.</item>
/// <item><c>env &lt;name&gt;...</c> — prints each named environment variable's value
/// ("&lt;unset&gt;" when missing), one per line. The child-environment probe.</item>
/// <item><c>exit &lt;code&gt;</c> — exits with the given code. The non-zero-exit probe.</item>
/// <item><c>flaky &lt;state-file&gt; &lt;fail-times&gt;</c> — counts its own invocations in the file
/// (cwd-relative, one number per run), prints <c>attempt N</c>, and exits 3 while N is at most
/// <c>fail-times</c>, then 0. The step-retry probe (card F-032): one step whose first attempts fail and
/// whose last one succeeds, with the attempt count as a fact in the working tree.</item>
/// <item><c>flaky-env &lt;state-file&gt; &lt;fail-times&gt; &lt;env-name&gt; &lt;report-file&gt;</c> — the same
/// retry probe <b>plus</b> an environment value written to a file (not to the log, where the redactor
/// would mask it): each attempt appends <c>N &lt;NAME&gt;=&lt;value&gt;</c>. The card F-032 fact that a retry
/// re-runs the step with the value it already had, never a freshly fetched one.</item>
/// </list>
/// </para>
/// </summary>
internal static class Program
{
    private const int UsageExitCode = 64;

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: ArgvEcho <echo|echo-each|probe|stream|spawn|env|exit> [args...]");
            return UsageExitCode;
        }

        switch (args[0])
        {
            case "echo":
                foreach (var argument in args[1..])
                {
                    Console.Out.WriteLine(argument);
                }

                return 0;

            case "echo-each":
                if (args.Length < 2 || !int.TryParse(args[1], out var echoEachDelay))
                {
                    return Usage("echo-each <ms> [args...]");
                }

                foreach (var argument in args[2..])
                {
                    Console.Out.WriteLine(argument);
                    Thread.Sleep(echoEachDelay);
                }

                return 0;

            case "probe":
                if (args.Length != 3)
                {
                    return Usage("probe <file> <text>");
                }

                // The newline is written literally, not as Environment.NewLine: the tests assert the
                // artifact's bytes (Q-002: artifact bytes are never rewritten), and a windows child
                // must produce the same bytes a POSIX child does.
                File.WriteAllText(args[1], args[2] + "\n");
                Console.Out.WriteLine("probe-written");
                return 0;

            case "append":
                // F-014: a marker file that records the order steps ran in (one line per step).
                if (args.Length != 3)
                {
                    return Usage("append <file> <text>");
                }

                // The newline is literal, for the same byte-determinism as the probe's.
                File.AppendAllText(args[1], args[2] + "\n");
                Console.Out.WriteLine("appended");
                return 0;

            case "write-slow":
                if (args.Length != 3 || !int.TryParse(args[1], out var slowDelay))
                {
                    return Usage("write-slow <ms> <stderr-text>");
                }

                Console.Out.WriteLine("stream-first");
                Console.Out.Flush();
                Thread.Sleep(slowDelay);
                Console.Error.WriteLine(args[2]);
                Thread.Sleep(slowDelay);
                Console.Out.WriteLine("stream-second");
                return 0;

            case "stream":
                if (args.Length != 2 || !int.TryParse(args[1], out var streamDelay))
                {
                    return Usage("stream <ms>");
                }

                Console.Out.WriteLine("stream-first");
                Console.Out.Flush();
                Thread.Sleep(streamDelay);
                Console.Out.WriteLine("stream-second");
                return 0;

            case "spawn":
                if (args.Length != 2 || !int.TryParse(args[1], out var spawnSleep))
                {
                    return Usage("spawn <ms>");
                }

                return SpawnGrandchildAndSleep(spawnSleep);

            case "env":
                if (args.Length < 2)
                {
                    return Usage("env <name>...");
                }

                foreach (var name in args[1..])
                {
                    Console.Out.WriteLine(Environment.GetEnvironmentVariable(name) ?? "<unset>");
                }

                return 0;

            case "exit":
                if (args.Length != 2 || !int.TryParse(args[1], out var exitCode))
                {
                    return Usage("exit <code>");
                }

                return exitCode;

            case "flaky-env":
                if (args.Length != 5 || !int.TryParse(args[2], out var failTimesWithEnv))
                {
                    return Usage("flaky-env <state-file> <fail-times> <env-name> <report-file>");
                }

                var envAttempts = File.Exists(args[1])
                    ? int.Parse(File.ReadAllText(args[1]).Trim(), System.Globalization.CultureInfo.InvariantCulture)
                    : 0;
                envAttempts++;
                File.WriteAllText(
                    args[1],
                    envAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
                File.AppendAllText(
                    args[4],
                    envAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " " + args[3] + "=" + (Environment.GetEnvironmentVariable(args[3]) ?? "<unset>") + "\n");
                Console.Out.WriteLine($"attempt {envAttempts}");
                return envAttempts <= failTimesWithEnv ? 3 : 0;

            case "flaky":
                // F-032: one step, several attempts. The counter file is the state, so the same argv can
                // fail and then succeed - which is what a retried step looks like from the outside.
                if (args.Length != 3 || !int.TryParse(args[2], out var failTimes))
                {
                    return Usage("flaky <state-file> <fail-times>");
                }

                var attempts = File.Exists(args[1])
                    ? int.Parse(File.ReadAllText(args[1]).Trim(), System.Globalization.CultureInfo.InvariantCulture)
                    : 0;
                attempts++;
                File.WriteAllText(
                    args[1],
                    attempts.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
                Console.Out.WriteLine($"attempt {attempts}");
                return attempts <= failTimes ? 3 : 0;

            default:
                return Usage($"unknown mode '{args[0]}'");
        }
    }

    private static int SpawnGrandchildAndSleep(int sleepMilliseconds)
    {
        var entryAssembly = System.Reflection.Assembly.GetEntryAssembly();
        var ownDll = entryAssembly?.Location;
        var dotnet = Environment.ProcessPath;
        if (ownDll is null || dotnet is null)
        {
            Console.Error.WriteLine("spawn: cannot locate the runtime or entry assembly.");
            return UsageExitCode;
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = dotnet,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(ownDll);
        startInfo.ArgumentList.Add("stream");
        startInfo.ArgumentList.Add(sleepMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var grandchild = System.Diagnostics.Process.Start(startInfo);
        if (grandchild is null)
        {
            Console.Error.WriteLine("spawn: the grandchild could not be started.");
            return UsageExitCode;
        }

        // Announce the grandchild so a test can check for it after the tree kill, then keep the
        // child alive long enough for the kill to be the only way everything ends.
        Console.Out.WriteLine($"grandchild-pid {grandchild.Id}");
        Console.Out.Flush();
        _ = grandchild.StandardOutput.ReadLine();
        Thread.Sleep(sleepMilliseconds);
        return 0;
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine($"usage: {message}");
        return UsageExitCode;
    }
}
