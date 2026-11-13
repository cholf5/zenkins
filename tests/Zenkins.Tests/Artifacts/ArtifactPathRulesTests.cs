using Xunit;
using Zenkins.Server.Artifacts;
using Zenkins.Shared.Artifacts;
using Zenkins.Shared.Pipelines;
using Zenkins.Worker;
using Zenkins.Worker.Lua;
using Zenkins.Worker.Pipelines;

namespace Zenkins.Tests.Artifacts;

/// <summary>
/// The <b>shared</b> path vector table (card F-016, TC-002): one set of declared paths and their verdicts,
/// run through the shared <see cref="ArtifactPathRules"/> implementation that the Worker (before upload) and
/// the Server (on every received relative path) both use. The upload tests then replay the same vectors as
/// <b>crafted HTTP uploads</b>, which is what makes the two validations independent rather than a copy.
/// </summary>
internal static class ArtifactPathVectors
{
    /// <summary>A path the rules accept, with its normalised form.</summary>
    internal sealed record Accepted(string Declared, string Normalized);

    /// <summary>A path the rules refuse, with a fragment its message must contain.</summary>
    internal sealed record Refused(string Declared, string ReasonFragment);

    internal static readonly Accepted[] Valid =
    [
        new("bin/Release", "bin/Release"),
        new("bin\\Release", "bin/Release"),
        new("bin/../Release", "Release"),
        new("./dist/app.dll", "dist/app.dll"),
        new("dist//app.dll", "dist/app.dll"),
    ];

    internal static readonly Refused[] Invalid =
    [
        new("../../etc/passwd", "leaves the artifact root"),
        new("..", "leaves the artifact root"),
        new("a/../../b", "leaves the artifact root"),
        new("a/b/../../../c", "leaves the artifact root"),
        new("/etc/passwd", "absolute path"),
        new("\\windows\\system32", "absolute path"),
        new("C:\\Windows\\System32\\config", "drive-qualified"),
        new("C:relative", "drive-qualified"),
        new("bin/app:dll", "contains ':'"),
        new("bin/app\u0000.dll", "control character"),
        new("", "is empty"),
        new("   ", "is empty"),
        new(".", "resolves to no path"),
        new(new string('a', ArtifactPathRules.MaxRelativePathLength + 1), "longer than"),
    ];
}

/// <summary>
/// Card F-016's path rules, the download-name sanitiser, the spec envelope and the definition-phase refusal:
/// the parts that must hold before any byte moves.
/// </summary>
[Trait("Category", "Artifacts")]
public sealed class ArtifactPathRulesTests
{
    [Theory]
    [MemberData(nameof(ValidVectors))]
    public void The_shared_rules_normalise_a_relative_path(string declared, string expected)
    {
        Assert.True(
            ArtifactPathRules.TryNormalize(declared, out var normalized, out var refusal),
            refusal?.Message);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [MemberData(nameof(InvalidVectors))]
    public void The_shared_rules_refuse_a_dangerous_path_and_name_it(string declared, string fragment)
    {
        Assert.False(ArtifactPathRules.TryNormalize(declared, out var normalized, out var refusal));
        Assert.Equal(string.Empty, normalized);
        Assert.NotNull(refusal);

        // The message names the path as declared: the whole point of a refusal is that the person who wrote
        // the pipeline can find it.
        Assert.Contains(declared, refusal!.Message, StringComparison.Ordinal);
        Assert.Contains(fragment, refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolving_a_normalised_path_inside_a_root_cannot_escape_it()
    {
        var root = Path.Combine(Path.GetTempPath(), $"zenkins-f016-resolve-{Guid.NewGuid():N}");
        try
        {
            // The normalised form is what reaches this call, but the second lock is exercised on its own: even
            // a caller that skipped normalisation cannot make the result leave the root.
            Assert.Equal(
                Path.Combine(root, "bin", "Release"),
                ArtifactPathRules.ResolveInside(root, "bin/Release"));

            foreach (var escaping in new[] { "../escape.txt", "bin/../../escape.txt", "..", "/etc/passwd" })
            {
                Assert.Throws<ArtifactPathException>(() => ArtifactPathRules.ResolveInside(root, escaping));
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void A_sibling_directory_sharing_a_run_id_prefix_is_never_inside_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "zenkins-f016-prefix", "a0000000-0000-4000-8000-000000000051");
        var sibling = root + "X";

        Assert.True(ArtifactPathRules.IsInside(root, Path.Combine(root, "f.txt")));
        Assert.True(ArtifactPathRules.IsInside(root, root));
        Assert.False(ArtifactPathRules.IsInside(root, sibling));
        Assert.False(ArtifactPathRules.IsInside(root, Path.Combine(sibling, "f.txt")));
    }

    [Theory]
    [InlineData("bundle.zip", "bundle.zip")]
    [InlineData("dist/bundle.zip", "dist_bundle.zip")]
    [InlineData("..", "fallback.bin")]
    [InlineData("a\"b\r\nX-Injected: 1.txt", "a_b__X-Injected_ 1.txt")]
    [InlineData("réport.txt", "r_port.txt")]
    [InlineData("", "fallback.bin")]
    [InlineData("   ", "fallback.bin")]
    public void The_download_name_is_sanitised(string declared, string expected)
    {
        var sanitized = ArtifactDownloadName.Sanitize(declared, "fallback.bin");

        Assert.Equal(expected, sanitized);
        Assert.DoesNotContain('\r', sanitized);
        Assert.DoesNotContain('\n', sanitized);
        Assert.DoesNotContain('"', sanitized);
        Assert.DoesNotContain('/', sanitized);
        Assert.DoesNotContain('\\', sanitized);
        Assert.All(sanitized, character => Assert.True(character < 128, "the name must stay ASCII"));
        Assert.True(sanitized.Length <= ArtifactDownloadName.MaxLength);
    }

    // ------------------------------------------------------------------
    // The spec envelope: `artifacts` is a declared step field (card F-016)
    // ------------------------------------------------------------------

    [Fact]
    public void A_step_may_declare_artifacts_and_the_field_round_trips_through_the_frozen_spec()
    {
        var spec = new PipelineSpec(
            PipelineSpecSchema.Version,
            "publish",
            [
                new PipelineStep("Build", Run: ["dotnet", "build"], Artifacts: ["bin/Release", "dist"]),
            ]);

        var json = PipelineSpecJson.Serialize(spec);
        Assert.Contains("\"artifacts\"", json, StringComparison.Ordinal);

        Assert.True(PipelineSpecJson.TryRead(json, out var parsed, out var error), error);
        Assert.Equal(["bin/Release", "dist"], parsed!.Steps[0].ArtifactPaths);
    }

    [Fact]
    public void The_spec_envelope_refuses_an_empty_or_oversized_artifact_list()
    {
        var empty = new PipelineSpec(
            PipelineSpecSchema.Version,
            "publish",
            [new PipelineStep("Build", Run: ["x"], Artifacts: [])]);
        Assert.False(PipelineSpecJson.TryRead(PipelineSpecJson.Serialize(empty), out _, out var emptyError));
        Assert.Contains("artifacts", emptyError, StringComparison.Ordinal);

        var tooMany = new PipelineSpec(
            PipelineSpecSchema.Version,
            "publish",
            [
                new PipelineStep(
                    "Build",
                    Run: ["x"],
                    Artifacts:
                    [
                        .. Enumerable.Range(0, PipelineSpecSchema.MaxArtifactsPerStep + 1)
                            .Select(index => $"path{index}"),
                    ]),
            ]);
        Assert.False(PipelineSpecJson.TryRead(PipelineSpecJson.Serialize(tooMany), out _, out var manyError));
        Assert.Contains("pipeline.steps[1].artifacts", manyError, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Definition phase: artifact() is refused and points at the declarative field
    // ------------------------------------------------------------------

    [Fact]
    public void Calling_artifact_from_the_definition_phase_is_refused_with_the_step_only_message()
    {
        var root = Path.Combine(Path.GetTempPath(), $"zenkins-f016-def-{Guid.NewGuid():N}");
        try
        {
            var work = new WorkDirectoryService(root).Prepare(Guid.NewGuid());
            File.WriteAllText(
                Path.Combine(work.RepositoryPath, "pipeline.lua"),
                "artifact(\"bin/Release\")\n"
                + "pipeline { name = \"p\", steps = { { name = \"B\", run = { \"true\" } } } }\n");

            var definitions = new PipelineDefinitionRunner(
                PipelineDefinitionRunner.CreateRuntime(PipelineRunner.CreateLogSink(_ => { })));

            var failure = Assert.Throws<PipelineDefinitionException>(() => definitions.Run(
                work,
                "pipeline.lua",
                DateTimeOffset.UtcNow.AddMinutes(1),
                TestContext.Current.CancellationToken));

            // AC-9: the same "available inside steps only" answer F-014's other I/O primitives give - and, for
            // artifact specifically, a pointer at the declarative field, because a v0.3 step body is C# argv.
            Assert.Contains("available inside steps only", failure.Message, StringComparison.Ordinal);
            Assert.Contains("artifacts = {", failure.Message, StringComparison.Ordinal);
            Assert.Equal(PipelineFailureReasons.PipelineInvalid, failure.FailureReason);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void The_step_reader_accepts_artifacts_and_refuses_an_unknown_key()
    {
        var accepted = PipelineSpecReader.Read(Map(
            ("name", Str("p")),
            ("steps", Seq(Map(
                ("name", Str("Build")),
                ("run", Seq(Str("dotnet"), Str("build"))),
                ("artifacts", Seq(Str("bin/Release"))))))));

        Assert.Equal(["bin/Release"], accepted.Steps[0].ArtifactPaths);

        var rejected = Assert.Throws<PipelineDefinitionException>(() => PipelineSpecReader.Read(Map(
            ("name", Str("p")),
            ("steps", Seq(Map(
                ("name", Str("Build")),
                ("run", Seq(Str("dotnet"))),
                ("artefacts", Seq(Str("bin/Release")))))))));

        Assert.Contains("unknown key 'artefacts'", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_step_reader_refuses_the_same_artifact_path_twice_with_the_field_path()
    {
        var rejected = Assert.Throws<PipelineDefinitionException>(() => PipelineSpecReader.Read(Map(
            ("name", Str("p")),
            ("steps", Seq(Map(
                ("name", Str("Build")),
                ("run", Seq(Str("dotnet"))),
                ("artifacts", Seq(Str("bin/Release"), Str("bin/Release")))))))));

        // A definition mistake, caught before the run starts (not as a 409 mid-upload), and it names both slots.
        Assert.Contains("pipeline.steps[1].artifacts[2]", rejected.Message, StringComparison.Ordinal);
        Assert.Contains("already declared by pipeline.steps[1].artifacts[1]", rejected.Message, StringComparison.Ordinal);
        Assert.Equal(PipelineFailureReasons.PipelineInvalid, rejected.FailureReason);
    }

    [Fact]
    public void The_spec_envelope_refuses_a_duplicated_artifact_path_too()
    {
        var spec = new PipelineSpec(
            PipelineSpecSchema.Version,
            "publish",
            [new PipelineStep("Build", Run: ["x"], Artifacts: ["dist", "dist"])]);

        Assert.False(PipelineSpecJson.TryRead(PipelineSpecJson.Serialize(spec), out _, out var error));
        Assert.Contains("declared twice", error, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> ValidVectors()
    {
        var data = new TheoryData<string, string>();
        foreach (var vector in ArtifactPathVectors.Valid)
        {
            data.Add(vector.Declared, vector.Normalized);
        }

        return data;
    }

    public static TheoryData<string, string> InvalidVectors()
    {
        var data = new TheoryData<string, string>();
        foreach (var vector in ArtifactPathVectors.Invalid)
        {
            data.Add(vector.Declared, vector.ReasonFragment);
        }

        return data;
    }

    /// <summary>A seam map - the shape a Lua table of named fields arrives as (<c>docs/lua-runtime.md</c> §5.2).</summary>
    private static LuaHostValue Map(params (string Key, LuaHostValue Value)[] entries) =>
        LuaHostValue.FromTable(LuaHostTable.Map(entries));

    /// <summary>A seam sequence - the shape a Lua list arrives as.</summary>
    private static LuaHostValue Seq(params LuaHostValue[] items) =>
        LuaHostValue.FromTable(LuaHostTable.Sequence(items));

    private static LuaHostValue Str(string value) => LuaHostValue.FromString(value);
}
