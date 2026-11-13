using Xunit;
using Zenkins.Shared.LogStreaming;
using Zenkins.Worker;
using Zenkins.Worker.Git;
using Zenkins.Worker.Pipelines;

namespace Zenkins.Tests.Steps;

/// <summary>
/// The run's local log files under concurrent writers (card F-015). This is the regression pin for an F-014
/// gap: the pipeline thread writes its own synthesized lines while each step's two output reader threads deliver
/// theirs, and <see cref="StreamWriter"/> is not thread-safe. Interleaved halves, a swallowed newline or a lost
/// line all used to be possible; <see cref="RunLogWriters"/> serializes them with one gate.
/// </summary>
[Trait("Category", "Steps")]
public sealed class StepLogWriterTests
{
    /// <summary>A deadlock cannot hang the suite: the burst has to finish inside this bound.</summary>
    private static readonly TimeSpan BurstDeadline = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Concurrent_writers_lose_no_line_split_no_line_and_do_not_deadlock()
    {
        const int writerCount = 6;
        const int linesPerWriter = 250;
        var directory = NewDirectory();

        try
        {
            using (var writers = new RunLogWriters(directory))
            {
                var tasks = new Task[writerCount];
                for (var writer = 0; writer < writerCount; writer++)
                {
                    var index = writer;
                    tasks[writer] = Task.Run(
                        () =>
                        {
                            for (var line = 0; line < linesPerWriter; line++)
                            {
                                writers.Write(
                                    line % 2 == 0 ? LogStream.StdOut : LogStream.StdErr,
                                    $"writer{index}-line{line:D3}");
                            }
                        },
                        TestContext.Current.CancellationToken);
                }

                // No deadlock, no starvation: the whole burst finishes inside the bound (a timeout here fails the
                // test instead of hanging the run).
                await Task.WhenAll(tasks).WaitAsync(BurstDeadline, TestContext.Current.CancellationToken);
            }

            var stdOut = await File.ReadAllLinesAsync(
                Path.Combine(directory, RunLogWriters.StdOutLogFileName), TestContext.Current.CancellationToken);
            var stdErr = await File.ReadAllLinesAsync(
                Path.Combine(directory, RunLogWriters.StdErrorLogFileName), TestContext.Current.CancellationToken);
            var all = stdOut.Concat(stdErr).ToArray();

            // No lost line and no duplicate: the gate does not drop or double anything.
            Assert.Equal(writerCount * linesPerWriter, all.Length);

            // No interleaving: every line is exactly one writer's line, with no half of another line inside it and
            // no swallowed newline (a split write would produce a line that fails this shape).
            Assert.All(all, line => Assert.Matches("^writer[0-9]+-line[0-9]{3}$", line));

            // ...and each writer contributed its whole batch.
            for (var writer = 0; writer < writerCount; writer++)
            {
                Assert.Equal(
                    linesPerWriter,
                    all.Count(line => line.StartsWith($"writer{writer}-", StringComparison.Ordinal)));
            }

            // The two streams stay in their own files.
            Assert.All(stdOut, line => Assert.Matches("^writer[0-9]+-line[0-9]{3}$", line));
            Assert.All(stdErr, line => Assert.Matches("^writer[0-9]+-line[0-9]{3}$", line));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task The_worker_crash_diagnostic_is_redacted_and_written_through_the_same_gate()
    {
        // The regression pin for the crash path (WorkerRunLoop's `could not be executed` line). A bug in the
        // pipeline layer raises an exception whose message can carry anything - including a value the run has
        // decrypted - so the diagnostic must go through the run's filter, the writer gate and the streaming buffer,
        // exactly like every other line. Reverting the loop to a raw WriteLine makes this test the thing that
        // notices.
        const string value = "crash-9f3a7c1b2e5d4a6f";
        var directory = NewDirectory();
        try
        {
            var redactor = new GitLogFilter();
            redactor.Register(value);
            var console = new StringWriter();
            var bufferErrorLog = new StringWriter();
            var runId = Guid.NewGuid();
            var stepId = Guid.NewGuid();
            string published;

            using (var writers = new RunLogWriters(directory))
            {
                var buffer = new RunLogBuffer(runId, capacity: 128, bufferErrorLog);
                var reporter = new RunCrashReporter(
                    runId,
                    redactor,
                    writers,
                    buffer,
                    () => stepId,
                    console);

                published = reporter.Report(
                    new InvalidOperationException($"the child process said {value} and then died."));

                // The line entered the run's streaming path (the same buffer every other line goes through).
                Assert.Equal(1, buffer.LastSeq);
            }

            Assert.Contains("could not be executed", published, StringComparison.Ordinal);
            Assert.DoesNotContain(value, published, StringComparison.Ordinal);

            var tempErr = await File.ReadAllTextAsync(
                Path.Combine(directory, RunLogWriters.StdErrorLogFileName), TestContext.Current.CancellationToken);
            Assert.Contains(RunCrashReporter.Prefix, tempErr, StringComparison.Ordinal);
            Assert.Contains("***", tempErr, StringComparison.Ordinal);
            Assert.DoesNotContain(value, tempErr, StringComparison.Ordinal);

            // The worker's own console channel receives the same redacted text - the surface a crash log is read
            // from is not allowed to carry the value either.
            var consoleText = console.ToString();
            Assert.Contains("could not be executed", consoleText, StringComparison.Ordinal);
            Assert.Contains("***", consoleText, StringComparison.Ordinal);
            Assert.DoesNotContain(value, consoleText, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task One_writer_keeps_its_own_order_and_the_lines_reach_the_right_file()
    {
        var directory = NewDirectory();
        try
        {
            using (var writers = new RunLogWriters(directory))
            {
                for (var line = 0; line < 50; line++)
                {
                    writers.Write(LogStream.StdOut, $"out-{line:D2}");
                    writers.Write(LogStream.StdErr, $"err-{line:D2}");
                }
            }

            var stdOut = await File.ReadAllLinesAsync(
                Path.Combine(directory, RunLogWriters.StdOutLogFileName), TestContext.Current.CancellationToken);
            var stdErr = await File.ReadAllLinesAsync(
                Path.Combine(directory, RunLogWriters.StdErrorLogFileName), TestContext.Current.CancellationToken);

            Assert.Equal(Enumerable.Range(0, 50).Select(line => $"out-{line:D2}"), stdOut);
            Assert.Equal(Enumerable.Range(0, 50).Select(line => $"err-{line:D2}"), stdErr);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task A_second_execution_of_the_same_run_starts_from_empty_files()
    {
        // A retried claim must not append to the previous attempt's logs: the lines would be misattributed.
        var directory = NewDirectory();
        try
        {
            using (var first = new RunLogWriters(directory))
            {
                first.Write(LogStream.StdOut, "first-attempt");
            }

            using (var second = new RunLogWriters(directory))
            {
                second.Write(LogStream.StdOut, "second-attempt");
            }

            var stdOut = await File.ReadAllTextAsync(
                Path.Combine(directory, RunLogWriters.StdOutLogFileName), TestContext.Current.CancellationToken);

            Assert.Contains("second-attempt", stdOut, StringComparison.Ordinal);
            Assert.DoesNotContain("first-attempt", stdOut, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"zenkins-f015-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
