using Xunit;

namespace Zenkins.Tests.RunUi;

/// <summary>
/// Card F-047's follow wiring. The scroll behaviour itself needs a browser (the repo has no browser
/// harness; D-004-B3 records why TestServer cannot host the circuit), so these facts pin what a
/// static pass can pin: the script is loaded before Blazor, the panel calls its three entry points,
/// and the script really owns the scroll/suppress contract. The view-model rules are pinned by
/// <see cref="RunLogControlsTests"/>.
/// </summary>
[Trait("Category", "RunUi")]
public sealed class RunConsoleFollowTests
{
    [Fact]
    public void App_loads_run_console_js_before_blazor()
    {
        var app = File.ReadAllText(Path.Combine(Root(), "src", "Server", "Components", "App.razor"));

        var script = app.IndexOf("js/run-console.js", StringComparison.Ordinal);
        var blazor = app.IndexOf("_framework/blazor.web.js", StringComparison.Ordinal);

        Assert.True(script >= 0, "App.razor does not load js/run-console.js.");
        Assert.True(blazor >= 0, "App.razor does not load Blazor.");
        Assert.True(script < blazor, "js/run-console.js must load before Blazor starts.");
    }

    [Fact]
    public void The_panel_exposes_the_follow_contract()
    {
        var panel = File.ReadAllText(Path.Combine(
            Root(), "src", "Server", "Components", "Pages", "Runs", "RunLogPanel.razor"));

        Assert.Contains("zenkinsConsole.init", panel, StringComparison.Ordinal);
        Assert.Contains("zenkinsConsole.afterRender", panel, StringComparison.Ordinal);
        Assert.Contains("zenkinsConsole.dispose", panel, StringComparison.Ordinal);
        Assert.Contains("OnConsoleScroll", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void The_script_defines_the_three_entry_points_and_suppresses_programmatic_scrolls()
    {
        var script = File.ReadAllText(Path.Combine(Root(), "src", "Server", "wwwroot", "js", "run-console.js"));

        Assert.Contains("init", script, StringComparison.Ordinal);
        Assert.Contains("afterRender", script, StringComparison.Ordinal);
        Assert.Contains("dispose", script, StringComparison.Ordinal);
        Assert.Contains("scrollTop = ", script, StringComparison.Ordinal);
        Assert.Contains("suppress", script, StringComparison.Ordinal);
        Assert.Contains("OnConsoleScroll", script, StringComparison.Ordinal);
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Zenkins.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the Zenkins repository root.");
    }
}
