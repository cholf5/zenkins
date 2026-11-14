using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// The committed surface inventory of card Q-006 (<c>docs/audits/q-006-surface-inventory.md</c>),
/// parsed into the rows the enforcement tests diff against the running application.
/// <para>
/// The parse is deliberately strict: a section heading or a malformed row is a hard failure, so an
/// accidental edit cannot silently shrink the audited surface. The static-asset marker row
/// (<c>every endpoint carrying StaticAssetDescriptor metadata</c>) is a classification rule, not a
/// route, and is represented by <see cref="InventorySection.AssetMarkerRow"/>.
/// </para>
/// </summary>
public static class TenantIsolationSurfaceInventory
{
    /// <summary>The committed inventory document (the audit's first deliverable).</summary>
    public const string RelativePath = "docs/audits/q-006-surface-inventory.md";

    /// <summary>The route cell of the one row that classifies endpoints by metadata instead of route.</summary>
    public const string AssetMarkerRoute = "(metadata) every endpoint carrying StaticAssetDescriptor metadata";

    public enum Section
    {
        /// <summary>The <c>## HTTP surfaces</c> table.</summary>
        HttpSurfaces,

        /// <summary>The <c>## SignalR hubs</c> table (route rows and hub-method rows).</summary>
        Hubs,

        /// <summary>The <c>## Non-tenant infrastructure</c> table.</summary>
        Infrastructure,

        /// <summary>The <c>## Stores, ids and credentials</c> table.</summary>
        Stores,
    }

    /// <summary>One row of any inventory table. Column meaning depends on <see cref="Section"/>.</summary>
    public sealed record InventoryRow(
        Section Section,
        string Method,
        string Route,
        string Hub,
        string HubMethod,
        string Probe);

    /// <summary>The parsed inventory: rows per section, in document order.</summary>
    public static IReadOnlyList<InventoryRow> Load()
    {
        var path = FindRepositoryRoot();
        var lines = File.ReadAllLines(Path.Combine(path, RelativePath));
        return Parse(lines);
    }

    /// <summary>
    /// Walks up from the test binary's directory until the repository root (the directory that holds
    /// the inventory document) is found, so the test runs from any output depth.
    /// </summary>
    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, RelativePath)))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                $"The committed surface inventory '{RelativePath}' was not found above "
                + $"{AppContext.BaseDirectory}. The audit fails loudly when the deliverable is missing.");
        }

        return directory.FullName;
    }

    public static IReadOnlyList<InventoryRow> Parse(string[] lines)
    {
        var rows = new List<InventoryRow>();
        var current = (Section?)null;
        var headerColumns = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var heading = Regex.Match(line, @"^##\s+(.+)$");
            if (heading.Success)
            {
                current = heading.Groups[1].Value.Trim() switch
                {
                    "HTTP surfaces" => Section.HttpSurfaces,
                    "SignalR hubs" => Section.Hubs,
                    "Non-tenant infrastructure" => Section.Infrastructure,
                    "Stores, ids and credentials" => Section.Stores,
                    _ => null,
                };
                headerColumns = 0;
                continue;
            }

            if (current is null || !line.StartsWith('|'))
            {
                continue;
            }

            var cells = ParseCells(line);
            if (cells.All(string.IsNullOrWhiteSpace) || cells.Count == 0)
            {
                continue;
            }

            // Skip the header and the |---| separator rows: the separator is dashes, the header's
            // first cell is a column title no data row starts with.
            if (cells.All(cell => Regex.IsMatch(cell, @"^:?-+:?$")))
            {
                continue;
            }

            if (IsHeaderRow(cells))
            {
                headerColumns = cells.Count;
                continue;
            }

            rows.Add(BuildRow(current.Value, cells, headerColumns));
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException(
                "The inventory document parsed to zero rows - it must keep at least the surface tables.");
        }

        return rows;
    }

    private static bool IsHeaderRow(IReadOnlyList<string> cells) =>
        cells[0] is "Method" or "Hub" or "Route" or "Surface";

    private static InventoryRow BuildRow(Section section, IReadOnlyList<string> cells, int headerColumns)
    {
        // Every table declares its column count in the header; a row that lost a cell is a broken
        // document, not a surface.
        if (headerColumns > 0 && cells.Count != headerColumns)
        {
            throw new InvalidOperationException(
                $"Inventory row in section '{section}' has {cells.Count} cells but the table declares "
                + $"{headerColumns}: '{string.Join(" | ", cells)}'.");
        }

        return section switch
        {
            // Method | Route | Tenant selection | Refusal contract | Probe
            Section.HttpSurfaces => new InventoryRow(
                section, cells[0], cells[1], Hub: string.Empty, HubMethod: string.Empty, Probe: cells[4]),

            // Hub | Route | Hub method | Tenant selection | Refusal contract | Probe
            Section.Hubs => new InventoryRow(
                section,
                Method: "*",
                Route: cells[1],
                Hub: cells[0],
                HubMethod: ParseHubMethod(cells[2]),
                Probe: cells[5]),

            // Route | Purpose | Probe
            Section.Infrastructure => new InventoryRow(
                section, Method: "*", Route: cells[0], Hub: string.Empty, HubMethod: string.Empty, Probe: cells[2]),

            // Surface | Location / shape | Isolation rule | Probe
            Section.Stores => new InventoryRow(
                section, Method: "*", Route: cells[0], Hub: string.Empty, HubMethod: string.Empty, Probe: cells[3]),

            _ => throw new UnreachableException("sections are mapped above"),
        };
    }

    /// <summary>
    /// The hub-method cell: either an em-dash row (transport/negotiate endpoints) or a method
    /// declaration like <c>JoinRunAsync(Guid runId)</c> - the method name is the identifier before
    /// the parenthesis.
    /// </summary>
    private static string ParseHubMethod(string cell)
    {
        var trimmed = cell.Trim();
        if (trimmed.StartsWith('—'))
        {
            return string.Empty;
        }

        var name = Regex.Match(trimmed, @"^([A-Za-z_][A-Za-z0-9_]*)\(");
        if (!name.Success)
        {
            throw new InvalidOperationException(
                $"The hub-method cell '{cell}' is neither a '—' endpoint row nor a method declaration.");
        }

        return name.Groups[1].Value;
    }

    private static List<string> ParseCells(string line)
    {
        var cells = new List<string>();
        // Split on unescaped pipes only: the ticket payload cells spell their internal pipes as ' \| ',
        // and splitting on those would shred the row. The escape is then unescaped per cell.
        var parts = Regex.Split(line, @"(?<!\\)\|");
        // parts[0] and parts[^1] surround the first and last cell.
        for (var i = 1; i < parts.Length - 1; i++)
        {
            cells.Add(parts[i].Trim().Replace("\\|", "|"));
        }

        return cells;
    }
}
