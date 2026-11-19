using System.Globalization;
using Xunit;
using Zenkins.Tests.Auth;

namespace Zenkins.Tests.Theme;

/// <summary>
/// The light-mode half of the theme switcher's base color choice.
/// <para>
/// The palette sheet paints every light canvas pure white — <c>--background</c>, <c>--card</c> and
/// <c>--popover</c> are <c>oklch(1 0 0)</c> for all five base colors — so picking slate or stone in
/// light mode moved a handful of low-chroma steps by 1–5 of 255 and the window looked untouched.
/// <c>styles/base-color-light.css</c> lifts each scale's own hue onto the four surface tokens, and
/// these facts hold it to the three promises that make it a fix rather than a new problem: the tint
/// is actually visible, zinc (the default) does not move, and dark mode still belongs to the palette.
/// </para>
/// </summary>
[Trait("Category", "Theme")]
public sealed class BaseColorLightSurfaceTests
{
    /// <summary>The base colors the palette ships with a light and a dark twin for.</summary>
    private static readonly string[] TintedBases = ["slate", "gray", "neutral", "stone"];

    /// <summary>The surfaces the tint owns. One value for all four: the canvas, its raised
    /// siblings and the sidebar rail are the same colour in this design, as the palette's white
    /// is.</summary>
    private static readonly string[] Surfaces = ["--background", "--card", "--popover", "--sidebar"];

    private const string PalettePath = ShellStylesheets.PalettePath;

    /// <summary>The white the palette paints every light canvas before this app's tint.</summary>
    private const string PaletteLightCanvas = "oklch(1 0 0)";

    [Fact(Timeout = 3 * 60 * 1000)]
    public async Task A_picked_base_color_paints_the_light_surfaces_off_white()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        var sheets = await ShellStylesheets.LoadAsync();

        // Zinc is the default (B-007 pins it to the library's own pair), so the fix must not move
        // it: a browser that picks zinc still renders exactly like one that picks nothing.
        var zinc = ServedThemeCascade.Resolve(sheets.Bodies, isDark: false).Tokens["--background"];
        Assert.Equal(PaletteLightCanvas, zinc);

        foreach (var baseColor in TintedBases)
        {
            var tokens = ServedThemeCascade.Resolve(sheets.Bodies, isDark: false, baseColor: baseColor).Tokens;

            var background = tokens["--background"];
            Assert.NotEqual(PaletteLightCanvas, background);

            var (l, c, _) = Oklch(background);
            Assert.True(
                l < 1,
                $"{baseColor}: --background is '{background}' — an L of 1 is white, so the pick would "
                + "still be invisible on a full-window canvas.");
            Assert.True(
                l is >= 0.975 and <= 0.990,
                $"{baseColor}: --background L is {l}, outside the 0.975–0.990 band that keeps a "
                + "near-white canvas while carrying a tint.");
            Assert.True(
                c is >= 0 and <= 0.030,
                $"{baseColor}: --background chroma is {c}, outside the 0–0.030 band a surface tint may use.");

            // Neutral is a gray scale with no hue at all, so its lift is lightness only.
            if (baseColor is "neutral")
            {
                Assert.Equal(0, c);
            }
            else
            {
                Assert.True(
                    c > 0,
                    $"{baseColor}: --background '{background}' carries no chroma, so the pick is not "
                    + "tinted by the base color at all.");
            }

            foreach (var surface in Surfaces)
            {
                Assert.True(tokens.TryGetValue(surface, out var value), $"{baseColor}: {surface} is undefined in light mode.");
                Assert.True(ServedThemeCascade.IsOpaqueColor(value), $"{baseColor}: {surface} resolves to '{value}', which the browser would not paint.");
                Assert.Equal(background, value);
            }
        }

        // The four tints stay distinguishable from each other: a lift that collapses two scales to
        // the same colour would make one of the five picks unreachable.
        var distinct = TintedBases
            .Select(baseColor => ServedThemeCascade.Resolve(sheets.Bodies, isDark: false, baseColor: baseColor).Tokens["--background"])
            .Concat([zinc])
            .ToArray();
        Assert.Equal(distinct.Length, distinct.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact(Timeout = 3 * 60 * 1000)]
    public async Task A_picked_base_color_still_resolves_to_the_palette_own_dark_values()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        var sheets = await ShellStylesheets.LoadAsync();
        var palette = sheets.ByHref[PalettePath];

        foreach (var baseColor in TintedBases)
        {
            var dark = ServedThemeCascade.Resolve(sheets.Bodies, isDark: true, baseColor: baseColor).Tokens;
            var light = ServedThemeCascade.Resolve(sheets.Bodies, isDark: false, baseColor: baseColor).Tokens;

            // The tint's selector is a bare attribute (0,1,0) loaded after the palette, so it wins
            // light mode on document order and loses dark mode to `.dark[data-base-color=…]`
            // (0,2,0). This is the fact that would catch a future selector like `:not(.dark)`, which
            // would raise the tint to (0,2,0) and let it paint a light canvas in dark mode for any
            // base color the palette stops shipping a dark twin for.
            var selector = $".dark[data-base-color=\"{baseColor}\"]";
            foreach (var surface in Surfaces)
            {
                var paletteDark = ServedThemeCascade.DeclaredValue(palette, selector, surface);
                Assert.NotNull(paletteDark);
                Assert.Equal(paletteDark, dark[surface]);
                Assert.NotEqual(light[surface], dark[surface]);
            }
        }
    }

    [Fact(Timeout = 3 * 60 * 1000)]
    public async Task Muted_text_still_clears_aa_on_the_tinted_light_canvas()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        var sheets = await ShellStylesheets.LoadAsync();

        foreach (var baseColor in TintedBases)
        {
            var tokens = ServedThemeCascade.Resolve(sheets.Bodies, isDark: false, baseColor: baseColor).Tokens;

            // The tint is a compromise: a darker canvas underneath the palette's own muted text
            // eats contrast, and AA is the floor this file is allowed to trade against.
            var contrast = ContrastRatio(
                ServedThemeCascade.Resolve(tokens["--muted-foreground"], tokens)!,
                ServedThemeCascade.Resolve(tokens["--background"], tokens)!);
            Assert.True(
                contrast >= 4.5,
                $"{baseColor}: --muted-foreground on the tinted --background is {contrast:0.00}:1 — "
                + "secondary text would fall below AA because of a cosmetic tint.");
        }
    }

    /// <summary>Parses the <c>oklch(L C H)</c> / <c>oklch(L C H / a)</c> syntax the palette uses.</summary>
    private static (double L, double C, double H) Oklch(string value)
    {
        var text = value.Trim();
        Assert.StartsWith("oklch(", text, StringComparison.OrdinalIgnoreCase);
        var body = text["oklch(".Length..].TrimEnd(')');
        var slash = body.IndexOf('/');
        if (slash >= 0)
        {
            body = body[..slash];
        }

        var parts = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Assert.True(parts.Length >= 3, $"'{value}' is not an oklch() triple.");
        return (
            double.Parse(parts[0], CultureInfo.InvariantCulture),
            double.Parse(parts[1], CultureInfo.InvariantCulture),
            double.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    /// <summary>WCAG 2.x relative contrast between two <c>oklch()</c> colours.</summary>
    private static double ContrastRatio(string first, string second)
    {
        var a = RelativeLuminance(Oklch(first));
        var b = RelativeLuminance(Oklch(second));
        var lighter = Math.Max(a, b);
        var darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance((double L, double C, double H) color)
    {
        var hue = color.H * Math.PI / 180;
        var a = color.C * Math.Cos(hue);
        var b = color.C * Math.Sin(hue);

        var l = Math.Pow(color.L + (0.3963377774 * a) + (0.2158037573 * b), 3);
        var m = Math.Pow(color.L - (0.1055613458 * a) - (0.0638541728 * b), 3);
        var s = Math.Pow(color.L - (0.0894841775 * a) - (1.2914855480 * b), 3);

        var r = (4.0767416621 * l) - (3.3077115913 * m) + (0.2309699292 * s);
        var g = (-1.2684380046 * l) + (2.6097574011 * m) - (0.3413193965 * s);
        var blue = (-0.0041960863 * l) - (0.7034186147 * m) + (1.7076147010 * s);

        return (0.2126 * Decode(r)) + (0.7152 * Decode(g)) + (0.0722 * Decode(blue));

        // The conversion above lands on gamma-encoded sRGB, which is what a screenshot shows; WCAG
        // luminance is defined on the linear channels, so decode before weighting them.
        static double Decode(double channel)
        {
            var clamped = Math.Clamp(channel, 0, 1);
            return clamped <= 0.04045 ? clamped / 12.92 : Math.Pow((clamped + 0.055) / 1.055, 2.4);
        }
    }
}
