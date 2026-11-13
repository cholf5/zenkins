using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Zenkins.Tests.Secrets;

/// <summary>
/// TC-002 (card F-012 AC 3): "no endpoint can return a stored value" is proven by enumerating the
/// running application's endpoint table, not by remembering. The second test feeds the same assertion
/// fabricated value routes - including one mounted outside <c>/secrets</c>, e.g. <c>/api/secrets/…</c>
/// - and a downgraded mutation, so a regression in the guard itself cannot leave the suite green.
/// </summary>
[Trait("Category", "Secrets")]
public sealed class SecretRouteShapeTests
{
    [Fact]
    public async Task The_application_exposes_only_the_page_and_the_three_post_mutations()
    {
        using var host = await SecretsTestHost.CreateAsync();
        var endpoints = host.Host.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var violations = SecretRouteShape.FindViolations(endpoints);
        Assert.True(
            violations.Count == 0,
            "The secret route surface deviates from the contract (docs/lua-runtime.md is unrelated; "
            + "the rule is: only the /secrets page and the three POST mutations, nothing that returns "
            + "a value). Violations: " + string.Join("; ", violations));

        // The allow-list is not vacuous: the mutations really are in the table.
        var templates = endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(SecretRouteShape.IsSecretRoute)
            .ToArray();

        Assert.Contains(SecretRouteShape.PageTemplate, templates);
        foreach (var mutation in SecretRouteShape.MutationTemplates)
        {
            Assert.Contains(mutation, templates);
        }

        // The one value route the product has: the worker's run-scoped fetch (card F-015). It is
        // bearer-authenticated, so it is not a browser surface.
        Assert.Contains(SecretRouteShape.WorkerFetchTemplate, templates);

        // And the count matches exactly: one page + three mutations + the worker fetch, so an added route of
        // any shape is visible in this number even before the allow-list names it.
        Assert.Equal(2 + SecretRouteShape.MutationTemplates.Count, templates.Length);
    }

    [Fact]
    public void The_assertion_catches_a_value_route_and_a_relaxed_mutation()
    {
        // A well-shaped table passes... including the one worker value route, which is deliberately
        // bearer-authenticated rather than cookie-authorized.
        Endpoint[] wellShaped =
        [
            Synthetic(SecretRouteShape.PageTemplate, methods: ["GET"]),
            Synthetic("/secrets/create", methods: ["POST"]),
            Synthetic("/secrets/{secretId:guid}/rotate", methods: ["POST"]),
            Synthetic("/secrets/{secretId:guid}/delete", methods: ["POST"]),
            Synthetic(SecretRouteShape.WorkerFetchTemplate, requiresAuthorization: false, methods: ["POST"]),
        ];
        Assert.Empty(SecretRouteShape.FindViolations(wellShaped));

        // ...while the worker template is matched exactly: a GET (a value route a browser could hit) and a
        // cookie-authorized variant are both red, so the allow-list entry cannot be widened by accident.
        Assert.NotEmpty(SecretRouteShape.FindViolations(
        [
            Synthetic(SecretRouteShape.PageTemplate, methods: ["GET"]),
            Synthetic(SecretRouteShape.WorkerFetchTemplate, requiresAuthorization: false, methods: ["GET"]),
        ]));
        Assert.NotEmpty(SecretRouteShape.FindViolations(
        [
            Synthetic(SecretRouteShape.PageTemplate, methods: ["GET"]),
            Synthetic(SecretRouteShape.WorkerFetchTemplate, requiresAuthorization: true, methods: ["POST"]),
        ]));

        // ...a fabricated value route is red (this is the mutation the guard exists for)...
        var withValueRoute = wellShaped
            .Append(Synthetic("/secrets/{secretId:guid}/value", methods: ["GET"]))
            .ToArray();
        var valueViolations = SecretRouteShape.FindViolations(withValueRoute);
        Assert.Contains(valueViolations, violation => violation.Contains("/value", StringComparison.Ordinal));

        // ...and the matcher is not a /secrets prefix check: a value route mounted at /api/secrets is
        // just as red, which is what a future "internal API" refactor must trip over.
        var withApiValueRoute = wellShaped
            .Append(Synthetic("/api/secrets/{secretId:guid}/value", methods: ["GET"]))
            .ToArray();
        Assert.Contains(
            SecretRouteShape.FindViolations(withApiValueRoute),
            violation => violation.Contains("/api/secrets/{secretId:guid}/value", StringComparison.Ordinal));

        // Case does not matter either: a route that says "Secret" in another spelling is still the
        // secret surface.
        var withCaseVariant = wellShaped
            .Append(Synthetic("/API/Secret/{secretId:guid}", methods: ["POST"]))
            .ToArray();
        Assert.NotEmpty(SecretRouteShape.FindViolations(withCaseVariant));

        // ...a "reveal" route under a plausible name is red as well...
        var withReveal = wellShaped
            .Append(Synthetic("/secrets/{secretId:guid}/reveal", methods: ["POST"]))
            .ToArray();
        Assert.NotEmpty(SecretRouteShape.FindViolations(withReveal));

        // ...and a mutation that silently accepts GET (or any extra verb) is red too.
        Endpoint[] relaxedMutation =
        [
            Synthetic(SecretRouteShape.PageTemplate, methods: ["GET"]),
            Synthetic("/secrets/{secretId:guid}/rotate", methods: ["GET", "POST"]),
        ];
        Assert.NotEmpty(SecretRouteShape.FindViolations(relaxedMutation));

        // An unauthenticated secret route is a violation even when its template is allowed.
        Endpoint[] unauthenticated =
        [
            Synthetic(SecretRouteShape.PageTemplate, requiresAuthorization: false, methods: ["GET"]),
            Synthetic("/secrets/create", requiresAuthorization: false, methods: ["POST"]),
        ];
        var authorizationViolations = SecretRouteShape.FindViolations(unauthenticated);
        Assert.Equal(2, authorizationViolations.Count);
        Assert.All(
            authorizationViolations,
            violation => Assert.Contains("authorization", violation, StringComparison.Ordinal));
    }

    private static RouteEndpoint Synthetic(
        string template,
        bool requiresAuthorization = true,
        params string[] methods)
    {
        var builder = new RouteEndpointBuilder(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(template),
            order: 0)
        {
            DisplayName = template,
        };

        builder.Metadata.Add(new HttpMethodMetadata(methods));
        if (requiresAuthorization)
        {
            builder.Metadata.Add(new AuthorizeAttribute());
        }

        return (RouteEndpoint)builder.Build();
    }
}
