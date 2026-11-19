using System.Reflection;
using Microsoft.AspNetCore.Http;
using Xunit;
using Zenkins.Server.Components.Pages.Runs;
using Zenkins.Server.Localization;

namespace Zenkins.Tests.RunUi;

/// <summary>
/// Card B-013's regression net: the run page's log panel must be able to build what it renders before
/// any lifecycle hook has run on it.
/// <para>
/// This is not abstract tidiness. On an interactive circuit the panel's <c>OnInitializedAsync</c>
/// awaits the log hub, and Blazor renders the component once while that await is pending - a render
/// that happens <b>before</b> <c>OnParametersSet</c>. A plan that only that hook filled was therefore
/// null at that render, and the whole run page fell into the ErrorBoundary ("The page failed to
/// render."). The browser pass recorded on the card proves the page end to end; this fact pins the
/// mechanism, so returning to a plan that only the hook fills turns it red without needing a browser.
/// </para>
/// </summary>
[Trait("Category", "RunUi")]
public sealed class LogPanelFirstRenderTests
{
    [Fact]
    public void The_log_panel_builds_a_plan_before_parameters_are_set()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);

        // No lifecycle call at all: this is the state the circuit renders the panel in while the
        // hub connection is still starting.
        var panel = new RunLogPanel();
        SetInjectedProperty(panel, "L10n", new Localizer(new LanguageContext(new HttpContextAccessor()), new TranslationCatalog()));

        var plan = typeof(RunLogPanel)
            .GetProperty("Plan", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(panel);

        Assert.NotNull(plan);
    }

    /// <summary>
    /// Assigns an <c>@inject</c>-generated property, which Razor emits as a private property the
    /// renderer fills; a bare component instance has nothing filling it.
    /// </summary>
    private static void SetInjectedProperty(object component, string name, object value)
    {
        var property = component.GetType().GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"The component has no injected property named {name}.");
        property.SetValue(component, value);
    }
}
