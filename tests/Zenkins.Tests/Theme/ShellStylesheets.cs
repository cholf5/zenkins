using Xunit;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Theme;

/// <summary>
/// The stylesheets the shell really links, fetched from the running host in <c>&lt;head&gt;</c>
/// order — the same list a browser would cascade over. Shared by the B-007 palette facts and the
/// Q-021 cascade-model facts so both read one definition of "the document".
/// </summary>
internal static class ShellStylesheets
{
    public const string AppThemePath = "styles/theme.css";
    public const string PalettePath = "_content/BlazorBlueprint.Components/css/themes.css";
    public const string FrameworkPath = "_content/BlazorBlueprint.Components/blazorblueprint.css";
    public const string AppCssPath = "app.css";

    public static async Task<(List<string> Bodies, Dictionary<string, string> ByHref)> LoadAsync()
    {
        using var host = new AuthTestHost();
        using var client = host.CreateClient();

        // The setup wizard (F-044) is static SSR, so this needs no signed-in session; App.razor
        // renders the same <head> for it and for the shell.
        var page = await AuthTestHost.GetStringAsync(client, "/setup");

        var hrefs = ServedThemeCascade.StylesheetHrefs(page);
        Assert.Contains(AppThemePath, hrefs);
        Assert.Contains(PalettePath, hrefs);
        Assert.Contains(FrameworkPath, hrefs);

        var bodies = new List<string>(hrefs.Count);
        var byHref = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var href in hrefs)
        {
            var body = await AuthTestHost.GetStringAsync(client, "/" + href);
            bodies.Add(body);
            byHref[href] = body;
        }

        return (bodies, byHref);
    }
}
