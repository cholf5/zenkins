using Xunit;
using Zenkins.Server.Workers;

namespace Zenkins.Tests.Workers;

/// <summary>
/// Unit tests for the F-004 token material: prefixes, hash shape and uniqueness.
/// The security-relevant property - only hashes stored - is asserted end to end in
/// <see cref="WorkerEndpointTests"/>; this pins the primitives themselves.
/// </summary>
[Trait("Category", "Workers")]
public sealed class WorkerTokenCryptoTests
{
    [Fact]
    public void Generated_tokens_use_the_requested_prefix_and_never_repeat()
    {
        var registration = WorkerTokenCrypto.Generate(WorkerTokenCrypto.RegistrationPrefix);
        var worker = WorkerTokenCrypto.Generate(WorkerTokenCrypto.WorkerPrefix);

        Assert.StartsWith("zreg_", registration, StringComparison.Ordinal);
        Assert.StartsWith("zwrk_", worker, StringComparison.Ordinal);
        Assert.Matches("^zreg_[A-Za-z0-9_-]+$", registration);
        Assert.Matches("^zwrk_[A-Za-z0-9_-]+$", worker);

        // 128 bits of entropy, base64url-encoded without padding.
        Assert.Equal(43, registration.Length - WorkerTokenCrypto.RegistrationPrefix.Length);
        Assert.NotEqual(registration, worker);

        var other = WorkerTokenCrypto.Generate(WorkerTokenCrypto.RegistrationPrefix);
        Assert.NotEqual(registration, other);
    }

    [Fact]
    public void Hashes_are_deterministic_64_char_lowercase_hex()
    {
        var token = WorkerTokenCrypto.Generate(WorkerTokenCrypto.RegistrationPrefix);

        var first = WorkerTokenCrypto.Hash(token);
        var second = WorkerTokenCrypto.Hash(token);

        Assert.Equal(64, first.Length);
        Assert.Matches("^[0-9a-f]{64}$", first);
        Assert.Equal(first, second);
        Assert.NotEqual(first, WorkerTokenCrypto.Hash(token + "x"));
    }
}
