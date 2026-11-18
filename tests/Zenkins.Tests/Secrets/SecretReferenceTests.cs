using Xunit;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Secrets;

namespace Zenkins.Tests.Secrets;

/// <summary>
/// Card F-040's shared half: the reference grammar, which kind may read which reference, and the payload
/// shapes inside the existing ciphertext. The worker's substitution and the Server's D-006 capture both read
/// these rules, which is why they live in one place.
/// </summary>
public sealed class SecretReferenceTests
{
    [Fact]
    public void A_whole_reference_still_means_todays_text_secret()
    {
        var reference = Parse(StepReference.SecretPrefix, "registry");

        Assert.Equal("registry", reference.Name);
        Assert.Equal(SecretPart.Whole, reference.Part);
        Assert.False(reference.IsFile);
        Assert.Equal("registry", reference.Recorded);

        Assert.True(reference.IsAllowedFor(SecretKinds.Text));
        Assert.False(reference.IsAllowedFor(SecretKinds.UsernamePassword));
        Assert.False(reference.IsAllowedFor(SecretKinds.File));
    }

    [Fact]
    public void A_pairs_halves_are_resolved_independently_and_recorded_as_written()
    {
        var username = Parse(StepReference.SecretPrefix, "registry.username");
        var password = Parse(StepReference.SecretPrefix, "registry.password");

        Assert.Equal(SecretPart.Username, username.Part);
        Assert.Equal(SecretPart.Password, password.Part);
        Assert.Equal("registry.username", username.Recorded);
        Assert.Equal("registry.password", password.Recorded);

        Assert.True(username.IsAllowedFor(SecretKinds.UsernamePassword));
        Assert.True(password.IsAllowedFor(SecretKinds.UsernamePassword));

        // The whole value of a pair is not a value: it has no single string to substitute.
        Assert.False(Parse(StepReference.SecretPrefix, "registry").IsAllowedFor(SecretKinds.UsernamePassword));
    }

    [Fact]
    public void A_file_reference_yields_a_path_and_takes_no_part()
    {
        var reference = Parse(StepReference.SecretFilePrefix, "deploy_key");

        Assert.True(reference.IsFile);
        Assert.Equal("deploy_key", reference.Name);
        Assert.Equal("file:deploy_key", reference.Recorded);
        Assert.True(reference.IsAllowedFor(SecretKinds.File));
        Assert.False(reference.IsAllowedFor(SecretKinds.Text));

        // "${secretFile:NAME.password}" is not a reference at all: a file has no halves.
        Assert.False(SecretReference.TryParse(StepReference.SecretFilePrefix, "deploy_key.password", out _));
    }

    [Fact]
    public void A_mismatch_says_which_kind_and_what_the_reference_asked_for()
    {
        var passwordOfText = Parse(StepReference.SecretPrefix, "token.password");
        Assert.False(passwordOfText.IsAllowedFor(SecretKinds.Text));
        Assert.Contains("text secret", passwordOfText.MismatchMessage(SecretKinds.Text), StringComparison.Ordinal);

        var file = Parse(StepReference.SecretFilePrefix, "deploy_key");
        var fileMessage = file.MismatchMessage(SecretKinds.File);
        Assert.Contains("file secret", fileMessage, StringComparison.Ordinal);
        Assert.Contains("${secretFile:deploy_key}", fileMessage, StringComparison.Ordinal);

        // The message says both what was asked for and what the secret is.
        Assert.Contains("${secretFile:deploy_key}", file.MismatchMessage(SecretKinds.Text), StringComparison.Ordinal);
    }

    [Fact]
    public void The_recorded_references_name_the_exact_forms_the_step_used()
    {
        // D-006: the Secrets page's "which pipelines use this" must be able to say "only the password half".
        var value = "user=${secret:registry.username} pass=${secret:registry.password} "
            + "key=${secretFile:deploy_key} plain=${secret:token} env=${env:HOME}";

        Assert.Equal(
            ["registry.username", "registry.password", "file:deploy_key", "token"],
            StepReference.ReferencesIn(value));

        // ...and the resolver's fetch list is the names, file secrets included.
        Assert.Equal(["registry", "registry", "deploy_key", "token"], StepReference.NamesFor(value));
    }

    [Fact]
    public void The_payload_round_trips_per_kind_and_text_stays_a_bare_string()
    {
        // Text is written as the bare plaintext, so a row written here is readable by a build that predates
        // the kinds - the compatibility the card asks for.
        Assert.Equal("s3cr3t", SecretPayload.ForText("s3cr3t").Encode());
        Assert.Equal("s3cr3t", SecretPayload.Decode(SecretKinds.Text, "s3cr3t").Value);

        var pair = SecretPayload.ForPair("me", "hunter2");
        var decodedPair = SecretPayload.Decode(SecretKinds.UsernamePassword, pair.Encode());
        Assert.Equal(("me", "hunter2"), (decodedPair.Username, decodedPair.Password));

        var file = SecretPayload.ForFile("-----BEGIN KEY-----");
        Assert.Equal("-----BEGIN KEY-----", SecretPayload.ForFile("-----BEGIN KEY-----").Content);
        Assert.Equal("-----BEGIN KEY-----", SecretPayload.Decode(SecretKinds.File, file.Encode()).Content);

        // A text secret whose value happens to be JSON is still text: the kind decides, never the payload.
        Assert.Equal("{ \"username\": \"x\" }", SecretPayload.Decode(SecretKinds.Text, "{ \"username\": \"x\" }").Value);
    }

    [Fact]
    public void The_two_secret_prefixes_cannot_both_match_at_one_position()
    {
        // Why StepReference.Match's order (file form first) is unobservable: the two prefixes diverge inside the
        // shorter one, so one position can only ever match one of them. This fact is what makes that true by
        // test rather than by argument - an edit that made "${secret:" a prefix of "${secretFile:" would make
        // the order load-bearing again, and would fail here first.
        Assert.False(
            StepReference.SecretFilePrefix.StartsWith(StepReference.SecretPrefix, StringComparison.Ordinal),
            "one secret prefix became a prefix of the other, so Match's order is no longer unobservable.");

        // Both forms resolve to their own prefix <b>and</b> to their own name. The two are asserted apart on
        // purpose: a prefix that matches tells nothing about where its name starts, and the offset is the half
        // that would read "File:key" if it ever drifted to the shorter prefix's length.
        var fileValue = "x${secretFile:key}";
        var file = StepReference.Match(fileValue, 1);
        Assert.Equal(StepReference.SecretFilePrefix, file.Prefix);
        Assert.Equal(1 + StepReference.SecretFilePrefix.Length, file.NameStart);
        Assert.Equal("key}", fileValue[file.NameStart..]);

        var secretValue = "x${secret:key}";
        var secret = StepReference.Match(secretValue, 1);
        Assert.Equal(StepReference.SecretPrefix, secret.Prefix);
        Assert.Equal(1 + StepReference.SecretPrefix.Length, secret.NameStart);
        Assert.Equal("key}", secretValue[secret.NameStart..]);

        // The numbers themselves, so a change to either constant is visible here: "${secretFile:" is 13
        // characters and "${secret:" is 9, and one character off either way fails the slices above.
        Assert.Equal(13, StepReference.SecretFilePrefix.Length);
        Assert.Equal(9, StepReference.SecretPrefix.Length);
    }

    private static SecretReference Parse(string prefix, string inner)
    {
        Assert.True(
            SecretReference.TryParse(prefix, inner, out var reference),
            $"'{prefix}{inner}}}' did not parse.");
        return reference;
    }
}
