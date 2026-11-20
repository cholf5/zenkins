using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Zenkins.Server.Components.Shared;
using Zenkins.Shared.LogStreaming;
using Zenkins.Tests.Localization;

namespace Zenkins.Tests.RunUi;

/// <summary>
/// Card F-047's line-rendering regression net. The old panel put every line in an inline
/// <c>&lt;span&gt;</c> and relied on a newline Razor trimmed away, so a whole step's output became one
/// long wrapped string. These facts render the component statically and count block elements, which
/// is the property that failed in the browser.
/// </summary>
[Trait("Category", "RunUi")]
public sealed class RunConsoleLinesTests
{
    [Fact]
    public async Task Each_entry_renders_in_its_own_block()
    {
        var html = await RenderAsync(
            Entries(Line(1, "one"), Line(2, "two"), Line(3, "three")),
            showTimestamps: false);

        Assert.Equal(3, Regex.Matches(html, "class=\"zk-console-line").Count);
        Assert.DoesNotContain("<pre", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(">one</span>", html, StringComparison.Ordinal);
        Assert.Contains(">two</span>", html, StringComparison.Ordinal);
        Assert.Contains(">three</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leading_spaces_and_empty_lines_are_preserved()
    {
        var html = await RenderAsync(
            Entries(Line(1, "  indented"), Line(2, string.Empty)),
            showTimestamps: false);

        Assert.Equal(2, Regex.Matches(html, "class=\"zk-console-line").Count);
        Assert.Contains(">  indented</span>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"zk-console-text\"></span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timestamps_render_only_when_enabled()
    {
        var timestamp = new DateTimeOffset(2026, 11, 20, 3, 4, 5, TimeSpan.Zero);
        var line = new LogLineMessage(1, LogStream.StdOut, "hello", timestamp, null);

        var without = await RenderAsync(Entries(line), showTimestamps: false);
        Assert.DoesNotContain("<time", without, StringComparison.Ordinal);

        var with = await RenderAsync(Entries(line), showTimestamps: true);
        Assert.Contains("<time", with, StringComparison.Ordinal);
        Assert.Contains(">03:04:05</time>", with, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_header_renders_as_a_clickable_button()
    {
        var stepId = Guid.NewGuid();
        var entries = new[]
        {
            new RunLogEntry("1. Build", Line(1, "compiling", stepId), stepId),
        };

        var html = await RenderAsync(entries, showTimestamps: false);

        Assert.Contains("class=\"zk-console-step\"", html, StringComparison.Ordinal);
        Assert.Contains("data-step=\"" + stepId + "\"", html, StringComparison.Ordinal);
        Assert.Contains(">1. Build</button>", html, StringComparison.Ordinal);
    }

    private static IReadOnlyList<RunLogEntry> Entries(params LogLineMessage[] lines) =>
        [.. lines.Select(line => new RunLogEntry(null, line))];

    private static LogLineMessage Line(long seq, string message, Guid? stepId = null) =>
        new(seq, LogStream.StdOut, message, DateTimeOffset.UtcNow, stepId);

    private static async Task<string> RenderAsync(IReadOnlyList<RunLogEntry> entries, bool showTimestamps)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TestText.English)
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(RunConsoleLines.Entries)] = entries,
            [nameof(RunConsoleLines.ShowTimestamps)] = showTimestamps,
            [nameof(RunConsoleLines.CurrentMatchSeq)] = null,
        });

        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<RunConsoleLines>(parameters)).ToHtmlString());
    }
}
