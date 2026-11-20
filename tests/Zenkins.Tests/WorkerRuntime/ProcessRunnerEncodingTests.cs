using System.Text;
using Xunit;
using Zenkins.Worker;

namespace Zenkins.Tests.WorkerRuntime;

/// <summary>
/// Card B-016: the Worker has to read what a native child actually wrote, not what the host's console
/// encoding assumes. The probes write raw bytes through the ArgvEcho asset, so the assertions are about
/// the bytes on the pipe and the strings the runner produced - never about a console's own encoder.
/// <para>
/// Every fact here runs on all three CI platforms. GBK is not a Windows-only concept in .NET: the
/// code-pages provider ships the table everywhere, so the same fallback path is exercised on macOS
/// and Linux with an explicitly configured 936.
/// </para>
/// </summary>
[Trait("Category", "WorkerRuntime")]
public sealed class ProcessRunnerEncodingTests
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [Fact]
    public async Task Utf8_chinese_output_is_not_mangled()
    {
        var collector = new OutputCollector();
        var exitCode = await RunBytesAsync("out", Encoding.UTF8.GetBytes("中文\n"), collector.Handler);

        Assert.Equal(0, exitCode);
        Assert.Equal(["中文"], collector.StdOutLines());
        Assert.Empty(collector.StdErrorLines());
    }

    [Fact]
    public async Task Gbk_output_falls_back_to_the_configured_code_page()
    {
        var collector = new OutputCollector();
        var gbk = Gbk();
        var exitCode = await RunBytesAsync("out", gbk.GetBytes("中文\n"), collector.Handler, gbk);

        Assert.Equal(0, exitCode);
        Assert.Equal(["中文"], collector.StdOutLines());
    }

    [Fact]
    public async Task Mixed_utf8_and_gbk_lines_are_both_readable()
    {
        var collector = new OutputCollector();
        var gbk = Gbk();
        var bytes = new byte[Encoding.UTF8.GetByteCount("中文\n") + gbk.GetByteCount("测试\n")];
        Encoding.UTF8.GetBytes("中文\n").CopyTo(bytes, 0);
        gbk.GetBytes("测试\n").CopyTo(bytes, Encoding.UTF8.GetByteCount("中文\n"));

        var exitCode = await RunBytesAsync("out", bytes, collector.Handler, gbk);

        Assert.Equal(0, exitCode);
        Assert.Equal(["中文", "测试"], collector.StdOutLines());
    }

    [Fact]
    public async Task A_utf8_bom_is_not_part_of_the_first_line()
    {
        var collector = new OutputCollector();
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("first\nsecond\n"))
            .ToArray();

        var exitCode = await RunBytesAsync("out", bytes, collector.Handler);

        Assert.Equal(0, exitCode);
        Assert.Equal(["first", "second"], collector.StdOutLines());
    }

    [Fact]
    public async Task Crlf_lf_and_cr_each_end_a_line()
    {
        var collector = new OutputCollector();
        var bytes = Encoding.UTF8.GetBytes("a\r\nb\nc\rd");

        var exitCode = await RunBytesAsync("out", bytes, collector.Handler);

        Assert.Equal(0, exitCode);
        Assert.Equal(["a", "b", "c", "d"], collector.StdOutLines());
    }

    [Fact]
    public async Task A_line_without_a_trailing_newline_is_still_delivered()
    {
        var collector = new OutputCollector();
        var exitCode = await RunBytesAsync("out", Encoding.UTF8.GetBytes("tail"), collector.Handler);

        Assert.Equal(0, exitCode);
        Assert.Equal(["tail"], collector.StdOutLines());
    }

    [Fact]
    public async Task Both_streams_keep_their_own_line_order()
    {
        var collector = new OutputCollector();
        using var runner = new ProcessRunner(
            new ProcessRunnerOptions
            {
                FileName = ArgvEchoBinary.DotNetPath,
                Arguments = ArgvEchoBinary.Command("write-slow", "10", "the-stderr-line"),
                OutputEncoding = StrictUtf8,
                FallbackEncoding = null,
            },
            collector.Handler);

        var exitCode = await runner.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(["stream-first", "stream-second"], collector.StdOutLines());
        Assert.Equal(["the-stderr-line"], collector.StdErrorLines());
    }

    private static async Task<int> RunBytesAsync(
        string stream,
        byte[] bytes,
        Action<ProcessOutputLine> onLine,
        Encoding? fallback = null)
    {
        using var runner = new ProcessRunner(
            new ProcessRunnerOptions
            {
                FileName = ArgvEchoBinary.DotNetPath,
                Arguments = ArgvEchoBinary.Command("bytes", stream, Convert.ToHexString(bytes)),
                OutputEncoding = StrictUtf8,
                FallbackEncoding = fallback,
            },
            onLine);

        return await runner.RunAsync(TestContext.Current.CancellationToken);
    }

    private static Encoding Gbk()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936);
    }
}
