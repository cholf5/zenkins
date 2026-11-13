using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Zenkins.Tests.Secrets;

/// <summary>
/// The route-shape assertion behind TC-002 (card F-012 AC 3): the endpoint table is the authority on
/// what the server can return, so it is enumerated and checked against an exact allow-list instead of
/// remembered. The allow-list has one page (which renders names and timestamps) and the three POST
/// mutations - nothing that could carry a stored value. Every route whose template mentions "secret"
/// must be one of them, wherever it is mounted: a value route added at <c>/api/secrets/…</c> fails the
/// same assertion as one under <c>/secrets</c>.
/// <para>
/// Kept as a pure function over <see cref="Endpoint"/>s so a test can feed it a fabricated
/// value-returning route and prove the assertion goes red (it must not be able to pass vacuously).
/// </para>
/// </summary>
internal static class SecretRouteShape
{
    /// <summary>The only read-only secret route: the Owner-only page.</summary>
    internal const string PageTemplate = "/secrets";

    /// <summary>The only mutations there may be. All must be POST.</summary>
    internal static IReadOnlyList<string> MutationTemplates { get; } =
    [
        "/secrets/create",
        "/secrets/{secretId:guid}/rotate",
        "/secrets/{secretId:guid}/delete",
    ];

    /// <summary>
    /// Whether a route template belongs to the secret surface: any template that mentions "secret"
    /// anywhere, not only the ones mounted under <c>/secrets</c>. The matcher is deliberately broader
    /// than today's route table so a value route added at <c>/api/secrets/…</c> (or any other mount
    /// point) fails the allow-list instead of slipping past a prefix check.
    /// </summary>
    internal static bool IsSecretRoute(string? template) =>
        template is not null
        && template.Contains("secret", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every way the secret surface deviates from the contract: an unexpected route (a value route, a
    /// download, a rename), a mutation that accepts anything but POST, or a secret route that is not
    /// authenticated. An empty result is the pass condition.
    /// </summary>
    internal static IReadOnlyList<string> FindViolations(IEnumerable<Endpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var violations = new List<string>();
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var template = endpoint.RoutePattern.RawText;
            if (!IsSecretRoute(template))
            {
                continue;
            }

            var methods = MethodsOf(endpoint);
            var description = $"{template} [{string.Join(",", methods)}]";

            if (!IsPageRoute(template!, methods)
                && !IsMutationInAllowList(template!, methods))
            {
                violations.Add(
                    $"{description}: only the page GET and the three POST mutations are allowed - "
                    + "no route that mentions 'secret' may return a stored value.");
                continue;
            }

            if (!RequiresAuthorization(endpoint))
            {
                violations.Add($"{description}: every secret route must require authorization.");
            }
        }

        return violations;
    }

    private static bool IsPageRoute(string template, IReadOnlyList<string> methods) =>
        string.Equals(template, PageTemplate, StringComparison.OrdinalIgnoreCase)
        // A Razor component endpoint is registered without method metadata, or as GET/HEAD, and Blazor's
        // static SSR endpoint also accepts POST for form posts. It renders names and timestamps only -
        // it has no handler that could return a stored value. Any other route template under /secrets
        // (a download, a value route) is not this template and fails the allow-list.
        && (methods.Count == 0 || methods.All(method =>
            IsReadMethod(method) || string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)));

    private static bool IsMutationInAllowList(string template, IReadOnlyList<string> methods) =>
        MutationTemplates.Any(allowed => string.Equals(allowed, template, StringComparison.OrdinalIgnoreCase))
        && methods.Count == 1
        && string.Equals(methods[0], "POST", StringComparison.OrdinalIgnoreCase);

    private static bool IsReadMethod(string method) =>
        string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> MethodsOf(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.ToArray() ?? [];

    private static bool RequiresAuthorization(Endpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0;
}
