using System.Text;
using Xunit;
using Zenkins.Server.Webhooks;

namespace Zenkins.Tests.Webhooks;

/// <summary>
/// Card F-017's pure rules: the signature comparison, the branch filter and the payload parser. No host,
/// no database - these are the rules the endpoint must never get wrong regardless of plumbing.
/// </summary>
[Trait("Category", "Webhooks")]
public sealed class WebhookRuleTests
{
    private const string Secret = "whsec_unit-test-secret";

    [Fact]
    public void A_github_signature_over_the_raw_bytes_verifies()
    {
        var body = Encoding.UTF8.GetBytes("""{"ref":"refs/heads/main","after":"abc"}""");
        var signature = WebhooksTestHost.Sign(body, Secret);

        Assert.True(WebhookSignature.IsValid(signature, null, body, Secret));

        // The same secret over different bytes does not: this is verification of the body, not of a shape.
        Assert.False(WebhookSignature.IsValid(
            signature,
            null,
            Encoding.UTF8.GetBytes("""{"after":"abc","ref":"refs/heads/main"}"""),
            Secret));
    }

    [Fact]
    public void A_gitlab_shared_token_verifies_without_the_hmac_header()
    {
        var body = Encoding.UTF8.GetBytes("{}");

        Assert.True(WebhookSignature.IsValid(null, Secret, body, Secret));
        Assert.False(WebhookSignature.IsValid(null, Secret + "x", body, Secret));
        Assert.False(WebhookSignature.IsValid(null, null, body, Secret));
    }

    [Fact]
    public void A_missing_malformed_or_wrong_length_signature_is_false_and_never_throws()
    {
        var body = Encoding.UTF8.GetBytes("{}");

        // Absent, empty, unprefixed, truncated and over-long all take the same fixed-time path and answer
        // false - a different length must not be a shortcut past the comparison.
        Assert.False(WebhookSignature.IsValid(null, null, body, Secret));
        Assert.False(WebhookSignature.IsValid(string.Empty, null, body, Secret));
        Assert.False(WebhookSignature.IsValid("deadbeef", null, body, Secret));
        Assert.False(WebhookSignature.IsValid("sha256=", null, body, Secret));
        Assert.False(WebhookSignature.IsValid("sha256=00", null, body, Secret));
        Assert.False(WebhookSignature.IsValid(new string('a', 5000), null, body, Secret));
    }

    [Fact]
    public void A_signature_is_case_insensitive_in_its_hex_digits()
    {
        var body = Encoding.UTF8.GetBytes("""{"ref":"refs/heads/main"}""");
        var signature = WebhooksTestHost.Sign(body, Secret);

        Assert.True(WebhookSignature.IsValid(
            "sha256=" + signature["sha256=".Length..].ToUpperInvariant(),
            null,
            body,
            Secret));
    }

    [Theory]
    [InlineData("main", "main", true)]
    [InlineData("main", "Main", false)]
    [InlineData("release/*", "release/1.2", true)]
    [InlineData("release/*", "release/", true)]
    [InlineData("release/*", "releases/1.2", false)]
    [InlineData("release/*", "main", false)]
    [InlineData("main,release/*", "release/9", true)]
    [InlineData("main,release/*", "feature/x", false)]
    public void The_branch_filter_matches_exact_names_and_trailing_wildcards(
        string filter,
        string branch,
        bool expected)
    {
        Assert.Equal(expected, WebhookBranchFilter.Matches(filter, branch, defaultBranch: "main"));
    }

    [Fact]
    public void An_empty_filter_selects_exactly_the_default_branch()
    {
        Assert.True(WebhookBranchFilter.Matches(string.Empty, "main", "main"));
        Assert.False(WebhookBranchFilter.Matches(string.Empty, "develop", "main"));
        Assert.True(WebhookBranchFilter.Matches(null, "trunk", "trunk"));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("main,release/*")]
    [InlineData("release/*")]
    [InlineData("feature/user-1")]
    public void Valid_filters_are_accepted(string filter)
    {
        Assert.Null(WebhookBranchFilter.Validate(filter, out var normalized));
        Assert.False(string.IsNullOrWhiteSpace(normalized));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("rel*ease")]
    [InlineData("release/")]
    [InlineData("main/")]
    [InlineData("main..x")]
    [InlineData(" main x")]
    [InlineData("/main")]
    [InlineData("-main")]
    [InlineData("main\\release")]
    public void Invalid_filters_are_refused(string filter)
    {
        Assert.Equal(WebhookErrorCodes.BranchFilterInvalid, WebhookBranchFilter.Validate(filter, out _));
    }

    [Fact]
    public void The_payload_parser_reads_the_three_shapes_and_refuses_garbage()
    {
        var after = WebhookPayloads.Commit('1');
        foreach (var body in new[]
                 {
                     WebhookPayloads.GitHub("main", after),
                     WebhookPayloads.Gitea("main", after),
                     WebhookPayloads.GitLab("main", after),
                 })
        {
            Assert.True(WebhookPayloadParser.TryParse(
                Encoding.UTF8.GetBytes(body),
                out var payload,
                out var error));
            Assert.Null(error);
            Assert.NotNull(payload);
            Assert.Equal("refs/heads/main", payload.Ref);
            Assert.Equal(after, payload.After);
            Assert.Equal(WebhookRefKind.Branch, WebhookPayloadParser.ReadRef(payload.Ref, out var branch));
            Assert.Equal("main", branch);
        }

        Assert.False(WebhookPayloadParser.TryParse(
            Encoding.UTF8.GetBytes("""{ "ref": "refs/heads/main" }"""), out _, out var missingAfter));
        Assert.Equal(WebhookPayloadErrorCodes.PayloadInvalid, missingAfter);

        Assert.False(WebhookPayloadParser.TryParse(Encoding.UTF8.GetBytes("[1,2]"), out _, out var notAnObject));
        Assert.Equal(WebhookPayloadErrorCodes.PayloadInvalid, notAnObject);
    }

    [Fact]
    public void A_tag_ref_is_not_a_branch_and_a_delete_is_all_zeroes()
    {
        Assert.Equal(WebhookRefKind.NotABranch, WebhookPayloadParser.ReadRef("refs/tags/v1.0", out _));
        Assert.Equal(WebhookRefKind.Invalid, WebhookPayloadParser.ReadRef("refs/heads/", out _));

        Assert.True(WebhookPayloadParser.IsDeleteEvent(WebhookPayloads.Deleted));
        Assert.False(WebhookPayloadParser.IsDeleteEvent(WebhookPayloads.Commit('1')));
    }

    [Fact]
    public void The_dedupe_key_is_a_bounded_hash_for_both_layers()
    {
        var body = Encoding.UTF8.GetBytes("""{"ref":"refs/heads/main"}""");

        var byId = WebhookDeliveryService.DedupeKey(body, "delivery-1");
        var byHash = WebhookDeliveryService.DedupeKey(body, null);

        Assert.StartsWith("id:", byId, StringComparison.Ordinal);
        Assert.StartsWith("hash:", byHash, StringComparison.Ordinal);
        Assert.Equal(67, byId.Length);
        Assert.Equal(69, byHash.Length);

        // The same id from two requests is the same key; a different id is not; no id falls back to the body.
        Assert.Equal(byId, WebhookDeliveryService.DedupeKey(Encoding.UTF8.GetBytes("other"), " delivery-1 "));
        Assert.NotEqual(byId, WebhookDeliveryService.DedupeKey(body, "delivery-2"));
        Assert.Equal(byHash, WebhookDeliveryService.DedupeKey(body, string.Empty));
    }
}
