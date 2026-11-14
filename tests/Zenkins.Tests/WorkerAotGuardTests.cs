using System.Collections.Immutable;
using System.Text.Json.Serialization.Metadata;
using Xunit;
using Zenkins.Shared.Artifacts;
using Zenkins.Shared.LogStreaming;
using Zenkins.Shared.RunCancellation;
using Zenkins.Worker;

namespace Zenkins.Tests;

/// <summary>
/// Card I-004's build-level pins, kept in the fast suite so they fail long before a publish:
/// <list type="bullet">
/// <item>The analyzer surface is on for good: the Worker carries <c>PublishAot</c> and
/// <c>Zenkins.Shared</c> carries <c>IsAotCompatible</c>, so every <c>dotnet build</c> runs the
/// trim/AOT/single-file analyzers and warnings-as-errors turns a hit into a build failure -
/// TC-002's "caught at build, not at runtime" gate. The one <c>NoWarn</c> in the solution
/// (MoonSharp's unannotated interop layer, justified in the csproj) is pinned to exactly the two
/// aggregate codes it was granted, so the hole cannot widen silently.</item>
/// <item>TC-002's missing-registration assertion: every type that crosses the worker's wire -
/// the HTTP payloads and the SignalR log-stream payload shapes - resolves source-generated
/// metadata from <c>WorkerJsonContext</c>. A new wire type without a registration fails here (and
/// would fail the AOT binary at the first exchange, which this test makes impossible to miss).</item>
/// <item>TC-003's grep, automated: no reflection-based <c>JsonSerializer</c> overload, no
/// reflection <c>ReadFromJsonAsync</c>, no <c>RegexOptions.Compiled</c> remains in worker source;
/// <c>JsonDocument.Parse</c> (a reflection-free DOM read) and typed <c>JsonTypeInfo</c> overloads
/// are the only JSON entry points.</item>
/// </list>
/// </summary>
public sealed class WorkerAotGuardTests
{
    // -----------------------------------------------------------------------------------------
    // The wire inventory (TC-002): a type on this list without source-generated metadata fails.
    // -----------------------------------------------------------------------------------------
    private static readonly ImmutableArray<Type> WireTypes =
    [
        // The worker API payloads (src/Worker/WorkerApiClient.cs).
        typeof(WorkerRegistrationRequest),
        typeof(WorkerHeartbeatRequest),
        typeof(WorkerRegistrationResponse),
        typeof(WorkerHeartbeatResponse),
        typeof(WorkerClaimResponse),
        typeof(WorkerAcceptRequest),
        typeof(WorkerAcceptResponse),
        typeof(WorkerDefinitionRequest),
        typeof(WorkerDefinitionResponse),
        typeof(WorkerCompleteRequest),
        typeof(WorkerCanceledRequest),
        typeof(WorkerStepRequest),
        typeof(WorkerStepResponse),
        typeof(WorkerSecretRequest),
        typeof(WorkerSecretResponse),
        typeof(WorkerArtifactErrorResponse),
        typeof(WorkerCancelRequest),

        // The artifact contracts (Zenkins.Shared, serialized by the worker).
        typeof(ArtifactCreateRequest),
        typeof(ArtifactCreateResponse),
        typeof(ArtifactFileEntry),
        typeof(ArtifactFinalizeRequest),
        typeof(ArtifactFinalizeResponse),

        // The SignalR log-stream payload (F-008) in every shape the hub protocol can name at
        // runtime - the batch is a List<LogLineMessage> today, the declared argument an
        // IReadOnlyList<LogLineMessage>, and the protocol serializes through the runtime type.
        typeof(LogLineMessage),
        typeof(List<LogLineMessage>),
        typeof(LogLineMessage[]),
        typeof(IReadOnlyList<LogLineMessage>),

        // The hub-protocol primitives: AppendLinesAsync's runId and cancelRun's (runId, reason).
        typeof(Guid),
        typeof(string),
    ];

    [Fact]
    public void Every_wire_type_has_source_generated_metadata()
    {
        var missing = new List<string>();
        foreach (var type in WireTypes)
        {
            // GetTypeInfo throws NotSupportedException for a type the context does not carry; the
            // non-null assertion makes a silent null equally loud.
            var typeInfo = WorkerJsonContext.Default.GetTypeInfo(type);
            if (typeInfo is null)
            {
                missing.Add(type.Name);
                continue;
            }

            Assert.Equal(type, typeInfo.Type);
        }

        Assert.True(
            missing.Count == 0,
            "wire types without source-generated JSON metadata (they would break the AOT binary "
            + "at runtime; register them in WorkerJsonContext): " + string.Join(", ", missing));
    }

    // -----------------------------------------------------------------------------------------
    // The project surface (TC-002): analyzers on every build, the one NoWarn pinned.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void The_worker_and_shared_projects_carry_the_aot_analyzer_surface()
    {
        var workerProject = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Worker", "Zenkins.Worker.csproj"));
        var sharedProject = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Shared", "Zenkins.Shared.csproj"));

        Assert.Contains("<PublishAot>true</PublishAot>", workerProject, StringComparison.Ordinal);
        Assert.Contains("<IsAotCompatible>true</IsAotCompatible>", sharedProject, StringComparison.Ordinal);

        // The card grants exactly one NoWarn, with its justification written at the site. The pin
        // makes widening it a deliberate, reviewable act instead of an accident.
        var noWarnLines = workerProject.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("<NoWarn>", StringComparison.Ordinal))
            .ToArray();
        var noWarn = Assert.Single(noWarnLines);
        Assert.Equal("<NoWarn>$(NoWarn);IL2104;IL3053</NoWarn>", noWarn);

        // And the worker source never silences an analyzer at the call site instead.
        Assert.Empty(SourceFiles("src", "Worker")
            .Where(file => File.ReadAllText(file).Contains("UnconditionalSuppressMessage", StringComparison.Ordinal))
            .ToArray());
    }

    // -----------------------------------------------------------------------------------------
    // TC-003: the wire stays source-generated; no JIT-regex codegen survives in worker source.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Worker_source_has_no_reflection_json_or_compiled_regex()
    {
        var violations = new List<string>();
        foreach (var file in SourceFiles("src", "Worker"))
        {
            var text = File.ReadAllText(file);
            var lines = text.Split('\n').Select(StripLineComment).ToArray();

            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                void Violate(string rule) => violations.Add($"{file}:{index + 1}: {rule}");

                if (line.Contains("JsonSerializer.Serialize", StringComparison.Ordinal)
                    || line.Contains("JsonSerializer.Deserialize", StringComparison.Ordinal)
                    || line.Contains("GetFromJsonAsync", StringComparison.Ordinal)
                    || line.Contains("PostAsJsonAsync", StringComparison.Ordinal))
                {
                    // The worker's only JSON entry points are JsonContent.Create(payload, typeInfo),
                    // ReadFromJsonAsync(typeInfo) and the reflection-free JsonDocument DOM.
                    Violate("reflection-based JsonSerializer entry point");
                }

                if (line.Contains("ReadFromJsonAsync", StringComparison.Ordinal))
                {
                    // A type-info overload is required; the statement must name a source-generated
                    // context (statements can span lines, so scan to the next semicolon).
                    var statement = line;
                    for (var scan = index; scan < lines.Length && !statement.Contains(';', StringComparison.Ordinal); scan++)
                    {
                        statement += lines[scan];
                    }

                    if (!statement.Contains("JsonContext.Default.", StringComparison.Ordinal))
                    {
                        Violate("ReadFromJsonAsync without a source-generated type info");
                    }
                }

                if (line.Contains("RegexOptions.Compiled", StringComparison.Ordinal))
                {
                    // JIT codegen path: does not survive NativeAOT. Regexes must be [GeneratedRegex].
                    Violate("RegexOptions.Compiled (use [GeneratedRegex])");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "reflection JSON / compiled-regex violations in worker source (NativeAOT): "
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void The_worker_regexes_are_source_generated()
    {
        // TC-002's second half: the only regex the worker ships is a [GeneratedRegex] (GitUrl's
        // scp-like pattern), so the matching behaviour is compile-time source, not a JIT artifact.
        var sources = SourceFiles("src", "Worker")
            .Select(file => (file, text: File.ReadAllText(file)))
            .ToArray();

        Assert.Contains(
            sources,
            entry => entry.text.Contains("[GeneratedRegex(", StringComparison.Ordinal)
                && entry.file.EndsWith("GitUrl.cs", StringComparison.Ordinal));
        Assert.Single(sources, entry => entry.text.Contains("[GeneratedRegex(", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------------------------

    private static string RepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (File.Exists(Path.Combine(directory, "Zenkins.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory)!;
        }

        Assert.Fail("the repository root (Zenkins.slnx) was not found above the test output.");
        return string.Empty;
    }

    /// <summary>
    /// Removes the line-comment tail of a line (doc comments included) so the scan judges code, not
    /// prose - the rules themselves name exactly what must never appear in executable statements.
    /// A "//" inside a string literal survives when it belongs to a URL scheme ("://").
    /// </summary>
    private static string StripLineComment(string line)
    {
        for (var index = 0; index < line.Length - 1; index++)
        {
            if (line[index] == '/' && line[index + 1] == '/' && (index == 0 || line[index - 1] != ':'))
            {
                return line[..index];
            }
        }

        return line;
    }

    private static IEnumerable<string> SourceFiles(params string[] parts)
    {
        var root = Path.Combine([RepoRoot(), .. parts]);
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
