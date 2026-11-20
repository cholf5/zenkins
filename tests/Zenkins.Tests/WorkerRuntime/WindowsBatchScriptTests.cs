using System.Text;
using Xunit;
using Zenkins.Worker.Pipelines;

namespace Zenkins.Tests.WorkerRuntime;

/// <summary>
/// Card B-016: <c>cmd.exe</c> reads a batch file in the code page the console is using, not UTF-8.
/// A Chinese build script therefore has two honest paths: write it in the host's OEM page when every
/// character fits (the normal case), or write UTF-8 and switch the page first when it does not.
/// <para>
/// The choice is pure, so it is pinned here on every platform; the execution facts that prove
/// <c>cmd.exe</c> agrees live in <see cref="WindowsScriptExecutionTests"/> and only run on Windows.
/// </para>
/// </summary>
[Trait("Category", "WorkerRuntime")]
public sealed class WindowsBatchScriptTests
{
    [Fact]
    public void Ascii_round_trips_through_the_oem_page_without_a_prologue()
    {
        var (bytes, hasPrologue) = WindowsBatchScript.Encode("echo hello", Encoding.ASCII);

        Assert.False(hasPrologue);
        Assert.Equal("echo hello", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Chinese_round_trips_through_gbk_without_a_prologue()
    {
        var gbk = Gbk();
        var (bytes, hasPrologue) = WindowsBatchScript.Encode("echo 中文", gbk);

        Assert.False(hasPrologue);
        Assert.Equal("echo 中文", gbk.GetString(bytes));
    }

    [Fact]
    public void Characters_outside_the_oem_page_fall_back_to_utf8_and_chcp()
    {
        var (bytes, hasPrologue) = WindowsBatchScript.Encode("echo 😀", Gbk());

        Assert.True(hasPrologue);
        var prologue = Encoding.ASCII.GetBytes("@chcp 65001 >nul\r\n");
        Assert.True(bytes.AsSpan().StartsWith(prologue));
        Assert.Equal("echo 😀", Encoding.UTF8.GetString(bytes, prologue.Length, bytes.Length - prologue.Length));
    }

    private static Encoding Gbk()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936);
    }
}
