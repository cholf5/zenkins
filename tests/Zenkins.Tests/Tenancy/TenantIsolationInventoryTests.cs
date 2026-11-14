using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Sdk;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Card Q-006's TC-001: the committed surface inventory
/// (<c>docs/audits/q-006-surface-inventory.md</c>) stays honest against the built application. The
/// audit fails loudly when a new endpoint or hub method is registered without entering the
/// inventory - and equally when a documented surface disappears, so stale rows cannot hide a gap.
/// <para>
/// The enumeration is the same table a client can reach: <see cref="EndpointDataSource"/> covers the
/// minimal APIs <em>and</em> the Blazor SSR pages (each page is an endpoint carrying
/// <see cref="Microsoft.AspNetCore.Components.Endpoints.ComponentTypeMetadata"/>); the hub methods
/// are the public methods declared on the two hub classes, which is what SignalR dispatches.
/// </para>
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed class TenantIsolationInventoryTests : IClassFixture<TenantProbeHostFixture>
{
    private readonly TenantProbeHostFixture _host;

    public TenantIsolationInventoryTests(TenantProbeHostFixture host) => _host = host;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public void The_endpoint_table_and_hub_methods_match_the_committed_inventory()
    {
        var inventory = TenantIsolationSurfaceInventory.Load();
        var endpoints = EnumerateEndpoints(_host);

        var failures = new List<string>();

        // ---- HTTP surfaces: every registered endpoint is documented, and every documented
        // route still exists. Method "*" rows (hubs, infrastructure) are matched by route alone;
        // the static-asset marker row is matched by metadata classification below.
        var documentedHttp = inventory
            .Where(row => row.Section is TenantIsolationSurfaceInventory.Section.HttpSurfaces)
            .Select(row => (row.Method, row.Route))
            .ToHashSet();
        var documentedHubRoutes = inventory
            .Where(row => row.Section is TenantIsolationSurfaceInventory.Section.Hubs && row.Route.StartsWith('/'))
            .Select(row => row.Route)
            .ToHashSet();
        var documentedInfrastructure = inventory
            .Where(row => row.Section is TenantIsolationSurfaceInventory.Section.Infrastructure
                && row.Route != TenantIsolationSurfaceInventory.AssetMarkerRoute)
            .Select(row => row.Route)
            .ToHashSet();

        var seenHttp = new HashSet<(string Method, string Route)>();
        var seenHubRoutes = new HashSet<string>();
        var seenInfrastructure = new HashSet<string>();
        var staticAssetCount = 0;

        foreach (var endpoint in endpoints)
        {
            if (endpoint.IsStaticAsset)
            {
                // Static assets carry no tenant data by construction; the inventory documents the
                // class once. The probes still assert the classification cannot hide an endpoint.
                staticAssetCount++;
                if (endpoint.HasAuthorize)
                {
                    failures.Add(
                        $"A static-asset endpoint carries [Authorize] (it is not the inventory's asset row): {endpoint.Route}");
                }

                continue;
            }

            // The Blazor-circuit prefixes come first: the circuit endpoints themselves are
            // registered with HubMetadata, which the hub classification below would otherwise
            // claim - and they are infrastructure, not the audit's product hubs.
            if (endpoint.IsInfrastructure)
            {
                if (!documentedInfrastructure.Contains(endpoint.Route))
                {
                    failures.Add($"Undocumented non-tenant infrastructure route: {endpoint.Route}");
                }

                seenInfrastructure.Add(endpoint.Route);
                continue;
            }

            if (endpoint.IsHubRoute)
            {
                if (!documentedHubRoutes.Contains(endpoint.Route))
                {
                    failures.Add($"Undocumented hub route: {endpoint.Route}");
                }

                seenHubRoutes.Add(endpoint.Route);
                continue;
            }

            var methods = endpoint.Methods.Length == 0 ? "*" : endpoint.Methods;
            var key = (methods, endpoint.Route);
            if (!documentedHttp.Contains(key))
            {
                failures.Add(
                    $"Undocumented tenant-reachable surface: {methods} {endpoint.Route}"
                    + (endpoint.HasAuthorize ? "" : " (note: no [Authorize] on the endpoint)"));
            }

            seenHttp.Add(key);
        }

        // A documented row that no longer matches a registered endpoint is a stale entry - the
        // inventory must describe what is reachable, not what once was.
        foreach (var (method, route) in documentedHttp.Except(seenHttp))
        {
            failures.Add($"Documented HTTP surface is no longer registered: {method} {route}");
        }

        foreach (var route in documentedHubRoutes.Except(seenHubRoutes))
        {
            failures.Add($"Documented hub route is no longer registered: {route}");
        }

        foreach (var route in documentedInfrastructure.Except(seenInfrastructure))
        {
            failures.Add($"Documented non-tenant route is no longer registered: {route}");
        }

        // The static-asset classification cannot rot: an app with styles and framework scripts
        // always has assets, and the marker row is the only place they are allowed to hide.
        if (staticAssetCount == 0)
        {
            failures.Add("No endpoint carried StaticAssetDescriptor metadata - the asset classification rule is stale.");
        }

        // ---- Hub methods: SignalR dispatches the public methods declared on the hub class.
        foreach (var (hubType, documentedMethods) in DocumentedHubMethods(inventory))
        {
            var actualMethods = HubMethodNames(hubType);
            var missing = documentedMethods.Except(actualMethods).ToList();
            var undocumented = actualMethods.Except(documentedMethods).ToList();
            foreach (var method in missing)
            {
                failures.Add($"Documented hub method no longer exists on {hubType.Name}: {method}");
            }

            foreach (var method in undocumented)
            {
                failures.Add($"Undocumented hub method on {hubType.Name}: {method}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "The built endpoint table and hub methods diverged from the committed inventory:\n"
            + string.Join("\n", failures.OrderBy(line => line, StringComparer.Ordinal)));
    }

    [Fact]
    public void Every_documented_surface_names_an_existing_probe_test()
    {
        var inventory = TenantIsolationSurfaceInventory.Load();
        var testMethodNames = TestMethodNames();

        var failures = new List<string>();
        foreach (var row in inventory)
        {
            if (row.Probe == "—")
            {
                // Rows that carry no tenant-owned data (account pages, static assets, the fallback)
                // are documented but need no probe; every tenant-reachable row names one.
                continue;
            }

            if (!testMethodNames.Contains(row.Probe))
            {
                failures.Add(
                    $"The inventory names probe '{row.Probe}' for '{row.Route}{row.HubMethod}', "
                    + "but no such test method exists in the test assembly.");
            }
        }

        Assert.True(
            failures.Count == 0,
            "The committed inventory references missing probes:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void The_committed_inventory_exists_in_the_repository()
    {
        // The deliverable itself: a missing inventory file is the loudest possible audit failure.
        var root = TenantIsolationSurfaceInventory.FindRepositoryRoot();
        Assert.True(
            File.Exists(Path.Combine(root, TenantIsolationSurfaceInventory.RelativePath)),
            $"The surface inventory '{TenantIsolationSurfaceInventory.RelativePath}' must be committed with the audit.");
        Assert.True(
            TenantIsolationSurfaceInventory.Load().Count > 40,
            "The inventory parsed implausibly few rows - the document must keep every section.");
    }

    private static List<(string Methods, string Route, bool IsStaticAsset, bool IsHubRoute,
        bool IsInfrastructure, bool HasAuthorize)> EnumerateEndpoints(TenantProbeHostFixture host)
    {
        var dataSource = host.Factory.Services.GetRequiredService<EndpointDataSource>();
        var result = new List<(string, string, bool, bool, bool, bool)>();
        foreach (var endpoint in dataSource.Endpoints)
        {
            var route = (endpoint as RouteEndpoint)?.RoutePattern.RawText
                ?? throw new InvalidOperationException(
                    $"The endpoint table contained a non-route endpoint ({endpoint.GetType().Name}).");

            var isStaticAsset = endpoint.Metadata.OfType<object>()
                .Any(metadata => metadata.GetType().Name == "StaticAssetDescriptor");
            var isFallback = endpoint.Metadata.Any(metadata => metadata.GetType().Name == "FallbackMetadata");
            var isHub = endpoint.Metadata.Any(metadata => metadata.GetType().Name == "HubMetadata");
            var methods = endpoint.Metadata.OfType<IHttpMethodMetadata>()
                .SelectMany(metadata => metadata.HttpMethods)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(method => method, StringComparer.Ordinal)
                .ToList();

            // The two non-tenant prefixes every Blazor circuit route lives under, plus the GET
            // fallback that renders the not-found page (no tenant rows). Account and error pages
            // stay in the HTTP table: they are real registered endpoints, documented with their
            // no-tenant-rows status there.
            var isInfrastructure = route.StartsWith("/_blazor", StringComparison.Ordinal)
                || route.StartsWith("/_framework", StringComparison.Ordinal)
                || isFallback;

            result.Add((string.Join("/", methods), route, isStaticAsset, isHub, isInfrastructure,
                endpoint.Metadata.OfType<IAuthorizeData>().Any()));
        }

        return result;
    }

    private static Dictionary<Type, List<string>> DocumentedHubMethods(
        IReadOnlyList<TenantIsolationSurfaceInventory.InventoryRow> inventory)
    {
        var serverAssembly = typeof(global::Program).Assembly;
        var rows = inventory
            .Where(row => row.Section is TenantIsolationSurfaceInventory.Section.Hubs
                && !string.IsNullOrEmpty(row.HubMethod))
            .GroupBy(row => row.Hub, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => row.HubMethod).Distinct(StringComparer.Ordinal).ToList());

        var result = new Dictionary<Type, List<string>>();
        foreach (var (hubName, methods) in rows)
        {
            var hubType = serverAssembly.GetTypes().FirstOrDefault(
                type => type.Name == hubName && typeof(Hub).IsAssignableFrom(type));
            if (hubType is null)
            {
                throw new XunitException(
                    $"The inventory names hub '{hubName}' but no such hub class exists in the Server assembly.");
            }

            result.Add(hubType, methods);
        }

        return result;
    }

    private static string[] HubMethodNames(Type hubType) =>
        hubType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName
                && method.Name is not (nameof(Hub.OnConnectedAsync) or nameof(Hub.OnDisconnectedAsync)))
            .Select(method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static HashSet<string> TestMethodNames() =>
        typeof(TenantIsolationInventoryTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            .Where(method => method.IsDefined(typeof(FactAttribute), inherit: false)
                || method.IsDefined(typeof(TheoryAttribute), inherit: false))
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
}
