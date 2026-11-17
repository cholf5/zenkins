using Xunit;

namespace Zenkins.Tests.Theme;

/// <summary>
/// Card Q-021: <see cref="ServedThemeCascade"/> must model the cascade for the sheets it reads, or
/// refuse to answer for anything it does not. B-007/Q-013 cross-checked it against real Chromium and
/// cleared it for the four values it then asserted (they matched exactly), but found three modelling
/// gaps that all erred toward false green: element selectors counted 0, <c>@layer</c> was ignored,
/// and <c>@media</c>/<c>@supports</c> applied unconditionally while <c>!important</c> stayed in the
/// value. These facts pin the three gaps shut, pin the refusals, and keep the reader answering where
/// it can.
/// <para>
/// The fixtures are hand-written sheets, not the served ones, except where a fact says otherwise.
/// That is deliberate: the <c>html:root</c> versus <c>[data-…]</c> collision the card's first gap
/// describes does not exist in this app's served sheets at all — none of <c>styles/theme.css</c>,
/// <c>blazorblueprint.css</c>, <c>css/themes.css</c> or <c>app.css</c> contains <c>html:root</c>, and
/// <c>app.css</c> declares no custom property — so it can only be pinned synthetically. Every fact
/// says where its expectation comes from: a browser measurement, or the cascade rules themselves.
/// </para>
/// </summary>
[Trait("Category", "Theme")]
public sealed class ThemeCascadeModelTests
{
    /// <summary>
    /// Q-021 AC 1/AC 4, the <c>html:root</c> versus <c>[data-…]</c> case.
    /// <para>
    /// <b>Basis</b>: this expectation is <em>not</em> a browser measurement. No served sheet contains
    /// <c>html:root</c>, so there is nothing in this app to measure today; the case is latent and
    /// therefore pinned synthetically. The expectation is CSS Cascading 5 §6.4's specificity
    /// ordering: specificity is the tuple (id, class/attribute/pseudo-class, type/pseudo-element)
    /// compared lexicographically, so <c>html:root</c> is (0,1,1) and <c>[data-base-color="zinc"]</c>
    /// is (0,1,0) and the higher tuple wins whatever the source order.
    /// </para>
    /// <para>
    /// Why it matters even though nothing here uses it: B-007's reader counted only classes,
    /// <c>:root</c> and <c>[</c>, which made those two tie and let document order hand the win to the
    /// palette. That is exactly the dimension D-001's "bare <c>.dark</c>, not <c>html.dark</c>"
    /// decision rests on — where the two happen to agree, by luck.
    /// </para>
    /// </summary>
    [Fact(Timeout = 60 * 1000)]
    public void Element_selectors_add_a_type_column_so_html_root_outranks_a_bare_attribute()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);

        const string appCss = """
            :root { --tie-between-two-roots: from-app-css; }
            html:root { --root-and-element: from-app-css; --two-classes-win: from-app-css; }
            html { --element-only: from-app-css; }
            """;

        const string paletteCss = """
            [data-base-color="zinc"] {
                --root-and-element: from-palette;
                --tie-between-two-roots: from-palette;
                --element-only: from-palette;
            }
            :root[data-base-color="zinc"] { --two-classes-win: from-palette; }
            """;

        string[][] orders = [[appCss, paletteCss], [paletteCss, appCss]];
        foreach (var sheets in orders)
        {
            var cascade = ServedThemeCascade.Resolve(sheets, isDark: false, baseColor: "zinc");

            // (0,1,1) beats (0,1,0) in either order: the palette is a bare attribute and cannot
            // outrank an element+root compound.
            Assert.Equal("from-app-css", cascade.Tokens["--root-and-element"]);

            // The converse guard against "an element selector is worth everything": two classes
            // (0,2,0) still beat one class plus one element, so the type column is a column and not
            // a trump card.
            Assert.Equal("from-palette", cascade.Tokens["--two-classes-win"]);

            // And a lone element (0,0,1) does not outrank a class/attribute (0,1,0), so `html` is
            // worth exactly one type point rather than a whole class.
            Assert.Equal("from-palette", cascade.Tokens["--element-only"]);
        }

        // The control: two (0,1,0) declarations are still decided by document order, so the
        // assertions above are about specificity and not about app.css always winning.
        Assert.Equal(
            "from-palette",
            ServedThemeCascade.Resolve([appCss, paletteCss], isDark: false, baseColor: "zinc").Tokens["--tie-between-two-roots"]);
        Assert.Equal(
            "from-app-css",
            ServedThemeCascade.Resolve([paletteCss, appCss], isDark: false, baseColor: "zinc").Tokens["--tie-between-two-roots"]);
    }

    /// <summary>
    /// Q-021 AC 2, the unlayered-versus-layered rule. For normal declarations an unlayered
    /// declaration outranks every layered one (CSS Cascading 5 §6.4), whichever sheet each sits in
    /// and whatever the document order — so treating a layer as "just another block" gets this
    /// backwards, which is how B-007's reader answered <c>16rem</c> for a value a browser computes as
    /// <c>15rem</c>.
    /// </summary>
    [Fact(Timeout = 60 * 1000)]
    public void An_unlayered_declaration_outranks_a_layered_one_even_when_the_layer_comes_later()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);

        string[] layeredLast =
        [
            ":root { --layer-probe: unlayered; }",
            "@layer base { :root { --layer-probe: layered; } }",
        ];

        Assert.Equal("unlayered", ServedThemeCascade.Resolve(layeredLast, isDark: false).Tokens["--layer-probe"]);

        // Same two declarations, other order: the answer is about layering, not about position.
        string[] layeredFirst =
        [
            "@layer base { :root { --layer-probe: layered; } }",
            ":root { --layer-probe: unlayered; }",
        ];

        Assert.Equal("unlayered", ServedThemeCascade.Resolve(layeredFirst, isDark: false).Tokens["--layer-probe"]);
    }

    /// <summary>
    /// Q-021 AC 2, the other half: among layers the layer declared first loses, and a bare
    /// <c>@layer a, b;</c> statement fixes that order before either block appears. Source order must
    /// never decide between two layers.
    /// </summary>
    [Fact(Timeout = 60 * 1000)]
    public void Layer_order_decides_between_two_layers_rather_than_source_order()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);

        const string css = """
            @layer first, second;
            @layer second { :root { --layer-probe: second; } }
            @layer first { :root { --layer-probe: first; } }
            """;

        Assert.Equal("second", ServedThemeCascade.Resolve([css], isDark: false).Tokens["--layer-probe"]);

        // Nested layers order inside their parent, and they are still weaker than unlayered rules.
        const string nested = """
            @layer outer {
                @layer inner { :root { --layered: inner; } }
            }
            :root { --layered: unlayered; }
            """;

        Assert.Equal("unlayered", ServedThemeCascade.Resolve([nested], isDark: false).Tokens["--layered"]);
    }

    /// <summary>
    /// The real-sheet pair that proves layers are modelled rather than skipped.
    /// <para>
    /// <c>blazorblueprint.css</c> declares <c>:root { --sidebar-width: 16rem; --sidebar-width-icon: 3rem }</c>
    /// inside <c>@layer base</c>, and <c>styles/theme.css</c> declares
    /// <c>:root { --sidebar-width: 15rem }</c> unlayered. B-007's comment claimed every sheet that
    /// declares these tokens was unlayered; it is not, and the old reader therefore answered
    /// <c>16rem</c> where Chromium computes <c>15rem</c> (the unlayered declaration wins outright,
    /// §6.4). <c>--sidebar-width-icon</c> is declared <em>only</em> inside <c>@layer base</c>, so
    /// answering it at all proves the layer is read and not skipped — together the two make
    /// "ignore the layered sheet" and "refuse everything layered" both impossible to pass.
    /// </para>
    /// </summary>
    [Fact(Timeout = 3 * 60 * 1000)]
    public async Task The_layered_sidebar_tokens_resolve_the_way_the_browser_computes_them()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
        var sheets = await ShellStylesheets.LoadAsync();

        foreach (var isDark in new[] { false, true })
        {
            var cascade = ServedThemeCascade.Resolve(sheets.Bodies, isDark);

            Assert.Equal(CascadeState.Declared, cascade.Answer("--sidebar-width").State);
            Assert.Equal("15rem", cascade.Tokens["--sidebar-width"]);

            Assert.Equal(CascadeState.Declared, cascade.Answer("--sidebar-width-icon").State);
            Assert.Equal("3rem", cascade.Tokens["--sidebar-width-icon"]);

            // Declared both layered and unlayered with the same value: no edit either way.
            Assert.Equal("18rem", cascade.Tokens["--sidebar-width-mobile"]);

            // Value hygiene on the real sheets: a resolved value is a value, never a declaration
            // with its priority flag still attached.
            Assert.DoesNotContain(cascade.Tokens.Values, value => value.Contains('!', StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Q-021 AC 3, the media half. A <c>prefers-color-scheme</c> block is answered only when the
    /// caller states the preference; the gate never does, because the app's dark mode comes from the
    /// <c>zenkins-theme</c> cookie and the server-rendered <c>class="dark"</c> (D-001), not from the
    /// OS. So the block makes its token <see cref="CascadeState.Unknown"/> instead of being applied
    /// in light mode as it used to be — and the refusal stops at that property.
    /// <para>
    /// The last two assertions are the reason this is not simply "refuse everything conditional":
    /// once the caller says what the query reports, the reader answers for both branches.
    /// </para>
    /// </summary>
    [Fact(Timeout = 60 * 1000)]
    public void A_media_query_the_reader_cannot_evaluate_makes_the_token_unknown_not_light()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);

        // Two schemes side by side: `--scheme-probe`'s media block comes last and would win if it
        // applied, so it cannot be answered; `--dominated-probe`'s comes first and loses to a later
        // unconditional declaration, so it can. Refusal is per candidate declaration, not per block.
        const string css = """
            @media (prefers-color-scheme: dark) { :root { --dominated-probe: dark; } }
            :root { --dominated-probe: light; }
            @media print { :root { --print-only: print; } }
            :root { --kept: red; }
            :root { --scheme-probe: light; }
            @media (prefers-color-scheme: dark) { :root { --scheme-probe: dark; } }
            """;

        var cascade = ServedThemeCascade.Resolve([css], isDark: false);

        var scheme = cascade.Answer("--scheme-probe");
        Assert.Equal(CascadeState.Unknown, scheme.State);
        Assert.Contains("prefers-color-scheme", scheme.Reason, StringComparison.Ordinal);
        Assert.Null(scheme.Value);

        // An unrecognised query is not "false" either: a print-only block is refused, not applied.
        var print = cascade.Answer("--print-only");
        Assert.Equal(CascadeState.Unknown, print.State);
        Assert.Contains("@media print", print.Reason, StringComparison.Ordinal);

        // The fail-closed half: a fact written the ordinary way cannot read a value here. This is
        // the assertion a value-reading fact makes, so it must not pass while the model is unsure.
        Assert.False(cascade.Tokens.ContainsKey("--scheme-probe"));
        Assert.False(cascade.Tokens.ContainsKey("--print-only"));

        // The refusal is scoped to the properties it can affect, never to the sheet.
        Assert.Equal("red", cascade.Tokens["--kept"]);

        // A conditional declaration that provably loses cannot change the answer, so the reader
        // still answers. Without this, "refuse anything inside an unknown @media" would pass every
        // assertion above while making the reader blind to a whole sheet's worth of declarations.
        Assert.Equal(CascadeState.Declared, cascade.Answer("--dominated-probe").State);
        Assert.Equal("light", cascade.Tokens["--dominated-probe"]);

        foreach (var prefersDark in new bool?[] { false, true })
        {
            var stated = ServedThemeCascade.Resolve([css], isDark: false, prefersDark: prefersDark);
            Assert.Equal("light", stated.Tokens["--dominated-probe"]);
        }

        Assert.Equal("light", ServedThemeCascade.Resolve([css], isDark: false, prefersDark: false).Tokens["--scheme-probe"]);
        Assert.Equal("dark", ServedThemeCascade.Resolve([css], isDark: false, prefersDark: true).Tokens["--scheme-probe"]);
    }

    /// <summary>
    /// Q-021 AC 3, the supports half, plus the distinction the three-state API exists for: refusing is
    /// per-property, and <see cref="CascadeState.Unknown"/> must not be mistakable for
    /// <see cref="CascadeState.NotDeclared"/> — otherwise <c>Assert.False(tokens.ContainsKey(…))</c>
    /// passes whether the reader is unsure or the sheet is silent.
    /// </summary>
    [Fact(Timeout = 60 * 1000)]
    public void An_unmodelled_supports_block_is_unknown_per_property_while_its_neighbour_still_answers()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);

        const string css = """
            :root { --guarded: unguarded-value; --plain: plain-value; }
            @supports (display: grid) { :root { --guarded: guarded-value; } }
            """;

        var cascade = ServedThemeCascade.Resolve([css], isDark: false);

        var guarded = cascade.Answer("--guarded");
        Assert.Equal(CascadeState.Unknown, guarded.State);
        Assert.Contains("@supports", guarded.Reason, StringComparison.Ordinal);
        Assert.Null(guarded.Value);

        // Not a whole-sheet refusal: the neighbour from the same rule still has a definite answer.
        Assert.Equal(CascadeState.Declared, cascade.Answer("--plain").State);
        Assert.Equal("plain-value", cascade.Tokens["--plain"]);

        // And unknown is genuinely different from "no such declaration".
        Assert.Equal(CascadeState.NotDeclared, cascade.Answer("--never-declared-anywhere").State);
        Assert.False(cascade.Tokens.ContainsKey("--guarded"));
        Assert.False(cascade.Tokens.ContainsKey("--never-declared-anywhere"));
        Assert.Contains("--guarded", cascade.UnknownTokens);
        Assert.DoesNotContain("--never-declared-anywhere", cascade.UnknownTokens);
    }

    /// <summary>
    /// The document model, which the old reader got wrong in both directions: a rule whose subject is
    /// not <c>&lt;html&gt;</c> never supplies <c>&lt;html&gt;</c>'s token (custom properties inherit
    /// down, never up), an unmodelled grouping at-rule is refused rather than applied, and an
    /// at-rule that holds no style rules is skipped rather than walked into.
    /// </summary>
    [Fact(Timeout = 60 * 1000)]
    public void A_rule_that_cannot_select_html_never_supplies_html_s_token()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);

        const string css = """
            body { --body-probe: body-value; }
            @container (min-width: 9000px) { :root { --container-probe: in-container; } }
            @keyframes probe { from { --keyframes-probe: from-1 } to { --keyframes-probe: to-2 } }
            :root { --keep: red; }
            """;

        var cascade = ServedThemeCascade.Resolve([css], isDark: false);

        // `body` is not the subject of a query about <html>, so this declares nothing for it.
        Assert.Equal(CascadeState.NotDeclared, cascade.Answer("--body-probe").State);
        Assert.False(cascade.Tokens.ContainsKey("--body-probe"));

        // @container is a grouping at-rule whose condition the reader cannot evaluate: refused.
        Assert.Equal(CascadeState.Unknown, cascade.Answer("--container-probe").State);
        Assert.Contains("@container", cascade.Answer("--container-probe").Reason, StringComparison.Ordinal);

        // @keyframes holds keyframe selectors, not element rules: nothing to apply.
        Assert.Equal(CascadeState.NotDeclared, cascade.Answer("--keyframes-probe").State);

        Assert.Equal("red", cascade.Tokens["--keep"]);
    }

    /// <summary>
    /// Q-021 AC 3, <c>!important</c>. It is modelled rather than left in the value: stripped off,
    /// ranked above any normal declaration regardless of specificity or order, and therefore never
    /// able to arrive at a colour check as <c>"oklch(1 0 0) !important"</c> — which the old reader
    /// both kept in the value and ranked as if it were normal, so a later <c>--p: blue</c> won a
    /// fight a browser gives to <c>red !important</c>.
    /// </summary>
    [Fact(Timeout = 60 * 1000)]
    public void Important_is_ranked_and_never_survives_into_the_value()
    {
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);

        string[] sheets =
        [
            """
            :root {
                --same-selector: red !important;
                --anchored: oklch(1 0 0) !important;
            }
            """,
            """
            :root { --same-selector: blue; }
            html:root { --anchored: oklch(0 0 0); }
            """,
        ];

        var cascade = ServedThemeCascade.Resolve(sheets, isDark: false);

        // Same selector, later normal declaration: importance still wins.
        Assert.Equal("red", cascade.Tokens["--same-selector"]);

        // More specific and later, but normal: still loses to importance.
        var anchored = cascade.Tokens["--anchored"];
        Assert.Equal("oklch(1 0 0)", anchored);
        Assert.DoesNotContain("!", anchored, StringComparison.Ordinal);
        Assert.True(ServedThemeCascade.IsOpaqueColor(anchored));

        // A value that still carries an unparsed `!` is not a colour the reader may accept, so a
        // regression that starts returning the raw declaration fails the colour assertions above.
        Assert.False(ServedThemeCascade.IsOpaqueColor("oklch(1 0 0) !important"));
    }
}
