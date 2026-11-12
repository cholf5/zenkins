using Xunit;
using Zenkins.Server.Auth;

namespace Zenkins.Tests.Auth;

/// <summary>
/// <c>ReturnUrl</c> is attacker-controlled query-string input. Every payload here must resolve to
/// the application's own origin after sanitization (most become the fallback "/").
/// </summary>
[Trait("Category", "Auth")]
public sealed class ReturnUrlSanitizerTests
{
    private static readonly Uri LoginUri = new("https://zenkins.example/account/login");

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("https://evil.com")]
    [InlineData("/\\evil.com")]
    [InlineData("%2f%2fevil.com")]
    [InlineData("/%2f%2fevil.com")]
    [InlineData("///evil.com")]
    [InlineData("..%2f..%2f")]
    [InlineData("http:/evil.com")]
    [InlineData("//evil.com/%2e%2e")]
    [InlineData("/path\r\nLocation: https://evil.com")]
    [InlineData("/path\nLocation: https://evil.com")]
    public void Payload_stays_on_the_application_origin(string payload)
    {
        var sanitized = ReturnUrlSanitizer.Sanitize(payload);

        Assert.DoesNotContain('\r', sanitized);
        Assert.DoesNotContain('\n', sanitized);

        var resolved = new Uri(LoginUri, sanitized);
        Assert.Equal(LoginUri.Scheme, resolved.Scheme);
        Assert.Equal(LoginUri.Authority, resolved.Authority);
    }

    [Theory]
    [InlineData("/projects", "/projects")]
    [InlineData("/projects?page=2", "/projects?page=2")]
    [InlineData("/", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    [InlineData("projects", "/")]
    public void Same_origin_paths_are_preserved(string? returnUrl, string expected) =>
        Assert.Equal(expected, ReturnUrlSanitizer.Sanitize(returnUrl));
}
