using Xunit;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Theme;

/// <summary>
/// Card B-007: the app must define the base token palette itself, so the default state — a browser
/// that never opened the theme popover and therefore carries no <c>data-base-color</c> /
/// <c>data-primary-color</c> on <c>&lt;html&gt;</c> — is themed instead of transparent.
/// <para>
/// These tests read the stylesheets the app really serves, in the order its own <c>&lt;head&gt;</c>
/// lists them, and resolve the cascade (<see cref="ServedThemeCascade"/>). That is the closest this
/// gate can get to the browser's computed style without shipping Node.js + Chromium into the
/// developer loop (docs/development.md §1), and it fails on exactly the failure D-007's review
/// measured: an undefined <c>--primary</c> makes <c>bb:bg-primary</c> paint
/// <c>rgba(0, 0, 0, 0)</c>, and a missing <c>--background</c> leaves <c>body</c> unpainted.
/// </para>
/// </summary>
[Trait("Category", "Theme")]
public sealed class BaseTokenPaletteTests
{
    /// <summary>
    /// The token set B-007's acceptance criterion names, plus the rest of the base block: a theme
    /// that defines half a palette is the bug, not a smaller version of the fix.
    /// </summary>
    private static readonly string[] RequiredTokens =
    [
        "--background", "--foreground", "--card", "--card-foreground", "--popover",
        "--popover-foreground", "--primary", "--primary-foreground", "--secondary",
        "--secondary-foreground", "--muted", "--muted-foreground", "--accent",
        "--accent-foreground", "--destructive", "--destructive-foreground", "--border", "--input",
        "--ring", "--sidebar", "--sidebar-foreground", "--sidebar-primary",
        "--sidebar-primary-foreground", "--sidebar-accent", "--sidebar-accent-foreground",
        "--sidebar-border", "--sidebar-ring",
    ];

    private const string FrameworkPath = ShellStylesheets.FrameworkPath;
    private const string PrimaryBackgroundSelector = ".bb\\:bg-primary";

    [Fact(Timeout = 3 * 60 * 1000)]
    public async Task A_browser_that_picks_nothing_gets_an_opaque_primary_and_background_in_both_modes()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        var sheets = await ShellStylesheets.LoadAsync();

        // A token this model cannot answer for is absent from Tokens on purpose (Q-021), so this
        // fact fails closed on a condition or selector the reader does not model instead of
        // reading a winner it is not entitled to.
        var lightCascade = ServedThemeCascade.Resolve(sheets.Bodies, isDark: false);
        var darkCascade = ServedThemeCascade.Resolve(sheets.Bodies, isDark: true);
        var light = lightCascade.Tokens;
        var dark = darkCascade.Tokens;

        var painted = new Dictionary<string, (string? Body, string? Primary)>(StringComparer.Ordinal);
        foreach (var (mode, cascade) in new[] { ("light", lightCascade), ("dark", darkCascade) })
        {
            var tokens = cascade.Tokens;
            foreach (var token in RequiredTokens)
            {
                var answer = cascade.Answer(token);
                Assert.True(
                    tokens.TryGetValue(token, out var value),
                    $"{mode}: {token} is {answer.State} with no data attributes — the palette a browser "
                    + "that never opened the theme popover sees is missing"
                    + (answer.Reason is null ? "." : $" ({answer.Reason})."));
                Assert.True(
                    ServedThemeCascade.IsOpaqueColor(value),
                    $"{mode}: {token} resolves to '{value}', which is not a color the browser paints.");
            }

            var body = ServedThemeCascade.Resolve(
                ServedThemeCascade.DeclaredValue(sheets.ByHref[FrameworkPath], "body", "background-color"),
                tokens);
            Assert.True(
                ServedThemeCascade.IsOpaqueColor(body),
                $"{mode}: body's background-color resolves to '{body}' — the page has no background.");

            var primary = ServedThemeCascade.Resolve(
                ServedThemeCascade.DeclaredValue(sheets.ByHref[FrameworkPath], PrimaryBackgroundSelector, "background-color"),
                tokens);
            Assert.True(
                ServedThemeCascade.IsOpaqueColor(primary),
                $"{mode}: a bb:bg-primary element computes background-color '{primary}' — the primary "
                + "button and the logo square would paint transparent.");

            painted[mode] = (body, primary);
        }

        // The other half of the bug: `class="dark"` used to flip only `color-scheme`, so both
        // passes resolved to the same (empty) colors. They must now differ.
        Assert.NotEqual(painted["light"], painted["dark"]);
        Assert.NotEqual(light["--background"], dark["--background"]);
        Assert.NotEqual(light["--primary"], dark["--primary"]);

        // AC: the default radius stays ThemeOptions.DefaultRadius (0.5rem), so the token set does
        // not quietly round (or square) the first frame.
        Assert.Equal("0.5rem", light["--radius"]);
    }

    [Fact(Timeout = 3 * 60 * 1000)]
    public async Task The_defaults_are_the_library_s_own_zinc_so_picking_zinc_changes_nothing()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        var sheets = await ShellStylesheets.LoadAsync();
        var bodies = sheets.Bodies;

        foreach (var isDark in new[] { false, true })
        {
            var nothing = ServedThemeCascade.Resolve(bodies, isDark);
            var zinc = ServedThemeCascade.Resolve(bodies, isDark, baseColor: "zinc");

            // Every token either path defines must agree, in both directions: a value that only one
            // path exists in is a divergence just as much as a value that differs. Walking Names
            // rather than Tokens matters after Q-021: a token the reader cannot answer for is absent
            // from Tokens, and skipping it here would hide a state difference between the two paths.
            var mode = isDark ? "dark" : "light";
            Assert.Equal(nothing.UnknownTokens, zinc.UnknownTokens);

            foreach (var token in nothing.Names.Union(zinc.Names, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var pickedNothing = nothing.Answer(token);
                var pickedZinc = zinc.Answer(token);
                Assert.Equal(pickedNothing.State, pickedZinc.State);

                if (pickedNothing.State == CascadeState.Unknown)
                {
                    // A refusal by both paths is consistent by definition; the assertion above is
                    // what stops one path refusing where the other answers.
                    continue;
                }

                Assert.True(nothing.Tokens.ContainsKey(token), $"{mode}: {token} exists only when zinc is picked.");
                Assert.True(zinc.Tokens.ContainsKey(token), $"{mode}: {token} exists only when nothing is picked.");

                var themed = ServedThemeCascade.Resolve(nothing.Tokens[token], nothing.Tokens);
                var zincValue = ServedThemeCascade.Resolve(zinc.Tokens[token], zinc.Tokens);
                Assert.Equal(zincValue, themed);
            }
        }
    }

    [Fact(Timeout = 3 * 60 * 1000)]
    public async Task A_picked_accent_still_overrides_the_default_primary()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        var sheets = await ShellStylesheets.LoadAsync();
        var bodies = sheets.Bodies;

        foreach (var isDark in new[] { false, true })
        {
            var nothing = ServedThemeCascade.Resolve(bodies, isDark).Tokens;
            var violet = ServedThemeCascade.Resolve(bodies, isDark, baseColor: "zinc", primaryColor: "violet").Tokens;

            // B-007's fix must not become the most specific rule on the page: the palette sheet has
            // to keep winning as soon as the switcher writes the attribute.
            Assert.NotEqual(nothing["--primary"], violet["--primary"]);
            Assert.True(ServedThemeCascade.IsOpaqueColor(violet["--primary"]));

            // The base gray scale is untouched by an accent choice.
            Assert.Equal(nothing["--background"], violet["--background"]);
        }
    }
}
