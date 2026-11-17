using Xunit;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Card Q-019's two-sided fact for <see cref="TenantPageScan"/>. One side proves the scan stops reading
/// a machine-generated payload as page text (the flake); the other proves it still reads rendered page
/// text as page text (the property the ten-page scan exists for), including the two value lengths that
/// come closest to the thresholds. Both sides run over a fragment that carries a real payload, so
/// neither can pass by the fragment being too small to matter.
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantPageScanTests
{
    /// <summary>
    /// The shape I-005 saw in the wild: a data-protection payload inside JSON, where the escaper wrote
    /// <c>+</c> as <c>\u002B</c> and the next base64 character was <c>e</c>, spelling <c>Beta</c>; the
    /// same run also spells it bare (<c>...CXBetaso...</c>).
    /// </summary>
    private const string EscapedPayloadFragment =
        """
        <div hx-post="/workers" data-protection="CfDJ8Nq7zR3vYdFSn0oPq2eYVk\u002B/4eATgK\u002BetaXIMvsRakkWlczn3/+Nu0QH3b+ate4H++ZahBCrCXBetasoeq66dT2QzWmJEK8HhJocRV3B3WmagsvImeyao9qWcarolz"></div>
        """;

    /// <summary>
    /// The other four fifths of the observed hits: no escape at all, a bare base64 run that happens to
    /// spell both of the fixture's short foreign values (<c>Beta</c>, <c>carol</c>).
    /// </summary>
    private const string BarePayloadFragment =
        """
        <div data-token="CfDJ8Qm2ZahBCrCXBetasoeq66dT2QzWcarolJEK8HhJocRV3B3WmagsvImeyao9Nu0QH3bate4Hz3n3"></div>
        """;

    /// <summary>
    /// The second round's gap: the antiforgery token is <b>base64url</b>, so it is one 198-symbol run of
    /// letters, digits, <c>-</c> and <c>_</c>. Every dash-separated segment below is shorter than the
    /// base64 minimum, which is exactly why a scan that only knows <c>+</c>/<c>/</c> left 105 of the
    /// token's 198 characters in front of its assertions.
    /// </summary>
    private const string Base64UrlPayloadFragment =
        """
        <meta name="request-verification-token" content="CfDJ8Nq7zR3vYdFSn0oPq2eYVk-4eATgKBetaXIMvsRakkWlczn3-Nu0QH3bate4HZahBCrCXBetaso_eq66dT2QzWmJEK8HhJocRV3B3Wmag-svImeyao9qWcarolzqWcarolz12">
        """;

    [Fact]
    public void An_escaped_payload_that_spells_the_foreign_name_does_not_trip_the_scan() =>
        AssertPayloadIsIgnored(EscapedPayloadFragment);

    [Fact]
    public void A_bare_base64_run_that_spells_the_foreign_name_does_not_trip_the_scan() =>
        AssertPayloadIsIgnored(BarePayloadFragment);

    [Fact]
    public void A_base64url_payload_that_spells_the_foreign_name_does_not_trip_the_scan() =>
        AssertPayloadIsIgnored(Base64UrlPayloadFragment);

    private static void AssertPayloadIsIgnored(string body)
    {
        // The collision is real: the page text contains the other organization's name verbatim, which
        // is what made the literal scan fail about once every four thousand page reads.
        Assert.Contains(TwoOrgsFixture.OrgBName, body, StringComparison.Ordinal);
        Assert.Contains(TwoOrgsFixture.OrgBOwnerUserId, body, StringComparison.Ordinal);

        // The payload is what the scan stops reading - not "nothing happened".
        var scanned = TenantPageScan.IgnoringMachineGeneratedPayloads(body);
        Assert.Contains(TenantPageScan.OpaquePayloadMarker, scanned, StringComparison.Ordinal);

        Assert.DoesNotContain(TwoOrgsFixture.OrgBName, scanned, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoOrgsFixture.OrgBOwnerUserId, scanned, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shape the round-2 reproducer caught: a base64url token whose first separator is an
    /// underscore and whose alphanumeric prefix is already 32 symbols. Deciding the encoding in the
    /// wrong order masks the prefix under the standard alphabet and leaves the <c>_</c>-separated tail
    /// - here the whole foreign name - in front of the assertions.
    /// </summary>
    [Fact]
    public void A_base64url_payload_whose_first_separator_is_an_underscore_is_masked_whole()
    {
        var prefix = new string('A', 32);
        var fragment = $"<meta name=\"request-verification-token\" content=\"CfDJ8{prefix}_qWcarolzBetaso{prefix}\">";
        Assert.Contains(TwoOrgsFixture.OrgBName, fragment, StringComparison.Ordinal);
        Assert.Contains(TwoOrgsFixture.OrgBOwnerUserId, fragment, StringComparison.Ordinal);

        var scanned = TenantPageScan.IgnoringMachineGeneratedPayloads(fragment);

        Assert.Equal(
            "<meta name=\"request-verification-token\" content=\"" + TenantPageScan.OpaquePayloadMarker + "\">",
            scanned);
    }

    [Theory]
    [InlineData("<td class=\"org\">{0}</td>")]
    [InlineData("<h1>{0}</h1>")]
    [InlineData("<a href=\"/organizations/switch?to={0}\" title=\"{0}\">{0}</a>")]
    [InlineData("<script>window.tenant = { \"name\": \"{0}\" };</script>")]
    [InlineData("<meta name=\"request-verification-token\" content=\"{0}\">")]
    [InlineData("<!--Blazor:{\"type\":\"server\",\"prerenderId\":\"x\",\"descriptor\":\"{0}\"}-->")]
    public void A_rendered_foreign_value_still_trips_the_scan(string markup)
    {
        // The payload is on the same page, so a mask that swallowed the page instead of the payload
        // would show up here as a false pass.
        var body = EscapedPayloadFragment
            + markup.Replace("{0}", TwoOrgsFixture.OrgBName, StringComparison.Ordinal);

        var scanned = TenantPageScan.IgnoringMachineGeneratedPayloads(body);

        Assert.Contains(TenantPageScan.OpaquePayloadMarker, scanned, StringComparison.Ordinal);
        Assert.Contains(TwoOrgsFixture.OrgBName, scanned, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two shapes where a hyphenated run would swallow an id if the alphabet were simply widened:
    /// a GUID inside a URL, and a GUID inside a generated DOM id (<c>rotate-{guid}</c> is a 43-symbol
    /// base64url run on the settings page). Both must stay page text, which is what keeps the
    /// <c>ProjectBId</c>/<c>BuildRunBId</c> assertions worth something.
    /// </summary>
    [Theory]
    [InlineData("<a href=\"/projects/{0}/settings\">x</a>")]
    [InlineData("<a href=\"/runs/{0}/log/download\">x</a>")]
    [InlineData("<div id=\"rotate-{0}\"></div>")]
    [InlineData("<div id=\"schedule-outcomes-{0}\"></div>")]
    public void A_foreign_id_inside_a_link_or_a_generated_dom_id_is_still_caught(string markup)
    {
        var body = "<!--Blazor:{\"descriptor\":\"\"}-->" + markup.Replace(
            "{0}", TwoOrgsFixture.GitCredentialBId.ToString(), StringComparison.Ordinal);

        var scanned = TenantPageScan.IgnoringMachineGeneratedPayloads(body);

        Assert.Contains(TwoOrgsFixture.GitCredentialBId.ToString(), scanned, StringComparison.Ordinal);
    }

    /// <summary>
    /// The scan stays literal about page text: all twelve scanned values survive in rendered markup,
    /// including the two longest ones - the 29-symbol ciphertext placeholder and the 36-symbol GUID -
    /// and nothing is masked at all.
    /// </summary>
    [Fact]
    public void Rendered_values_are_not_masked()
    {
        string[] rendered =
        [
            $"<td>{TwoOrgsFixture.ProjectBName}</td>",
            $"<td>{TwoOrgsFixture.ProjectBRepositoryUrl}</td>",
            $"<td>{TwoOrgsFixture.WorkerBName}</td>",
            $"<td>{TwoOrgsFixture.SecretBName}</td>",
            $"<td>{TwoOrgsFixture.SecretBCiphertext}</td>",
            $"<td>{TwoOrgsFixture.GitCredentialBName}</td>",
            $"<td>{TwoOrgsFixture.WebhookBName}</td>",
            $"<td>{TwoOrgsFixture.OrgBName}</td>",
            $"<td>{TwoOrgsFixture.OrgBOwnerUserId}</td>",
            $"<td>{TwoOrgsFixture.ProjectBId}</td>",
            $"<td>{TwoOrgsFixture.BuildRunBId}</td>",
            "<td>beta-line</td>",
            $"<td>{TwoOrgsFixture.SecretBCiphertext} / {TwoOrgsFixture.GitCredentialBCiphertext} / "
            + $"{TwoOrgsFixture.WebhookBCiphertext}</td>",
            "<td>alpha-app</td>",
            "<td>alpha-line</td>",
        ];

        var body = string.Concat(rendered);
        var scanned = TenantPageScan.IgnoringMachineGeneratedPayloads(body);

        Assert.DoesNotContain(TenantPageScan.OpaquePayloadMarker, scanned, StringComparison.Ordinal);
        Assert.Equal(body, scanned);
        foreach (var value in new[]
                 {
                     TwoOrgsFixture.ProjectBName,
                     TwoOrgsFixture.ProjectBRepositoryUrl,
                     TwoOrgsFixture.WorkerBName,
                     TwoOrgsFixture.SecretBName,
                     TwoOrgsFixture.SecretBCiphertext,
                     TwoOrgsFixture.GitCredentialBName,
                     TwoOrgsFixture.WebhookBName,
                     TwoOrgsFixture.OrgBName,
                     TwoOrgsFixture.OrgBOwnerUserId,
                     TwoOrgsFixture.ProjectBId.ToString(),
                     TwoOrgsFixture.BuildRunBId.ToString(),
                     "beta-line",
                 })
        {
            Assert.Contains(value, scanned, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The boundary of the base64url half, stated rather than implied: one symbol short of the minimum is
    /// page text, the minimum itself is a payload. The dashes keep the standard-base64 half out of it
    /// (its longest segment stays at 31), so this measures the 40 alone.
    /// </summary>
    [Fact]
    public void A_base64url_run_is_a_payload_from_the_documented_minimum_on()
    {
        var below = new string('q', 31) + "-" + new string('q', 7); // 39 symbols
        var at = new string('q', 31) + "-" + new string('q', 8);    // 40 symbols

        Assert.Equal(39, below.Length);
        Assert.Equal(40, at.Length);

        Assert.Equal(below, TenantPageScan.IgnoringMachineGeneratedPayloads(below));
        Assert.Equal(
            TenantPageScan.OpaquePayloadMarker,
            TenantPageScan.IgnoringMachineGeneratedPayloads(at));
    }

    /// <summary>
    /// The standard-base64 half keeps its own, lower minimum: the escaped <c>+</c>/<c>/</c> payload
    /// I-005 saw is measured by it, and a slash run is not a base64url run at all.
    /// </summary>
    [Fact]
    public void A_standard_base64_run_is_a_payload_from_its_own_minimum_on()
    {
        var below = new string('/', TenantPageScan.MinimumStandardBase64PayloadSymbols - 1);
        var at = new string('/', TenantPageScan.MinimumStandardBase64PayloadSymbols);

        Assert.Equal(below, TenantPageScan.IgnoringMachineGeneratedPayloads(below));
        Assert.Equal(
            TenantPageScan.OpaquePayloadMarker,
            TenantPageScan.IgnoringMachineGeneratedPayloads(at));
    }

    /// <summary>
    /// Known limitation, pinned as behaviour so a change here is visible rather than silent: a value that
    /// touches a payload is inside the payload's run and is masked with it. This is the price of not
    /// reading payloads as page text, and it is why the mask is limited to runs that are payload-shaped.
    /// </summary>
    [Theory]
    [InlineData("<div data-x=\"{0}AALBeta\"></div>")]
    [InlineData("<div data-x=\"BetazAAL{0}\"></div>")]
    public void Known_limitation_a_value_touching_a_payload_is_masked_with_it(string markup)
    {
        var payload = "ZahBCrCXBetasoeq66dT2QzWmJEK8HhJocRV3B3WmagsvImeyao9";
        var body = markup.Replace("{0}", payload, StringComparison.Ordinal);
        Assert.Contains(TwoOrgsFixture.OrgBName, body, StringComparison.Ordinal);

        var scanned = TenantPageScan.IgnoringMachineGeneratedPayloads(body);

        Assert.Contains(TenantPageScan.OpaquePayloadMarker, scanned, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoOrgsFixture.OrgBName, scanned, StringComparison.Ordinal);
    }

    /// <summary>
    /// Known limitation, second half: a rendered value that is itself a 40+ symbol base64url run with no
    /// GUID in it reads as a payload. The fixture has no such value (its longest are the 29-symbol
    /// ciphertext and the 36-symbol GUID, both proven visible above), and the real pages' runs of this
    /// shape are the UI library's generated ids.
    /// </summary>
    [Fact]
    public void Known_limitation_a_long_hyphenated_rendered_value_is_masked()
    {
        var value = $"bb-Beta-panel-viewport-{new string('d', 32)}";
        Assert.True(value.Length >= TenantPageScan.MinimumBase64UrlPayloadSymbols);

        var scanned = TenantPageScan.IgnoringMachineGeneratedPayloads($"<div class=\"{value}\"></div>");

        Assert.Equal(
            $"<div class=\"{TenantPageScan.OpaquePayloadMarker}\"></div>",
            scanned);
    }
}
