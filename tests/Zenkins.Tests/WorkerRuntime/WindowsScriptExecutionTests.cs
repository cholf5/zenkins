using System.Text;
using Xunit;
using Zenkins.Worker;
using Zenkins.Worker.Pipelines;

namespace Zenkins.Tests.WorkerRuntime;

/// <summary>
/// Card B-016's end-to-end facts: <c>cmd.exe</c>, a real Windows child, reads the batch file the plan
/// wrote and prints the text the user typed. These only run on Windows; the pure choice is pinned on
/// every platform by <see cref="WindowsBatchScriptTests"/>.
/// </summary>
[Trait("Category", "WorkerRuntime")]
public sealed class WindowsScriptExecutionTests
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [Fact]
    public async Task A_gbk_batch_file_prints_chinese_unchanged()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows-only fact: cmd.exe is the subject.");
        }

        var oem = WindowsCodePages.Oem;
        Assert.NotNull(oem);
        var script = "@echo off\r\necho 中文\r\n";
        var (bytes, hasPrologue) = WindowsBatchScript.Encode(script, oem);
        Assert.False(hasPrologue);

        var lines = await RunBatchAsync(bytes);

        Assert.Contains("中文", lines);
    }

    [Fact]
    public async Task A_utf8_emoji_batch_file_prints_unchanged_via_chcp()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows-only fact: cmd.exe is the subject.");
        }

        var oem = WindowsCodePages.Oem;
        Assert.NotNull(oem);
        var script = "@echo off\r\necho 😀\r\n";
        var (bytes, hasPrologue) = WindowsBatchScript.Encode(script, oem);
        Assert.True(hasPrologue);

        var lines = await RunBatchAsync(bytes);

        Assert.Contains("😀", lines);
    }

    private static async Task<string[]> RunBatchAsync(byte[] scriptBytes)
    {
        var directory = Path.Combine(Path.GetTempPath(), "zenkins-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "step.cmd");
        await File.WriteAllBytesAsync(path, scriptBytes, TestContext.Current.CancellationToken);

        try
        {
            var collector = new OutputCollector();
            using var runner = new ProcessRunner(
                new ProcessRunnerOptions
                {
                    FileName = "cmd.exe",
                    Arguments = ["/d", "/s", "/c", path],
                    WorkingDirectory = directory,
                    OutputEncoding = StrictUtf8,
                    FallbackEncoding = WindowsCodePages.Oem,
                },
                collector.Handler);

            var exitCode = await runner.RunAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, exitCode);
            return collector.StdOutLines();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
