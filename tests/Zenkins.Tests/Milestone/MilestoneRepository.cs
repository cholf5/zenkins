using Zenkins.Tests.Git;
using Zenkins.Tests.Pipelines;
using Zenkins.Tests.WorkerRuntime;

namespace Zenkins.Tests.Milestone;

/// <summary>
/// Q-002's fixture builder: a <b>real</b> Git repository with a <c>pipeline.lua</c> at its root, served
/// from a <b>bare</b> origin the worker clones, plus a working copy the test commits into. Everything is
/// local (a <c>file://</c> URL, no network, no credentials): the milestone's claim is about a real
/// repository, so the fixture is one.
/// <para>
/// <b>Shape.</b> <c>main</c> carries three commits (a README, the pipeline definition, a notes file) and
/// <c>feature</c> carries two more - "a few commits on two branches" as the card asks. A variant is just
/// another committed <c>pipeline.lua</c>, so the failure taxonomy drives the same repository the happy
/// path drives, exactly as a developer would.
/// </para>
/// <para>
/// A <c>file://</c> URL is what the worker clones in F-007's process E2E, and it is the one transport a
/// network-less machine always has. It cannot go through the project form (which refuses a server-side
/// read of the server's disk), so the tests seed the project row - a fixture pointing a row at a
/// repository it can actually reach, not a bypassed product rule.
/// </para>
/// </summary>
internal sealed class MilestoneRepository : IDisposable
{
    private readonly GitTestDirectory _directory;

    private MilestoneRepository(
        GitTestDirectory directory,
        string sourcePath,
        string barePath,
        string mainTip,
        string featureTip)
    {
        _directory = directory;
        SourcePath = sourcePath;
        BarePath = barePath;
        MainTip = mainTip;
        FeatureTip = featureTip;
        Environment = GitCli.Environment(directory);
    }

    /// <summary>The working copy commits are made in (the bare origin has no tree).</summary>
    public string SourcePath { get; }

    /// <summary>The bare origin, which is what the worker really clones.</summary>
    public string BarePath { get; }

    /// <summary>The <c>file://</c> URL of the bare origin - the project's repository URL.</summary>
    public string FileUrl => new Uri(BarePath).AbsoluteUri;

    /// <summary>The commit at the tip of <c>main</c> after the last push.</summary>
    public string MainTip { get; private set; }

    /// <summary>The commit at the tip of <c>feature</c>.</summary>
    public string FeatureTip { get; private set; }

    /// <summary>The isolated git environment every fixture command runs with.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; }

    /// <summary>
    /// Creates the fixture: <c>main</c> gets a README, <paramref name="pipelineLua"/> and a notes file;
    /// <c>feature</c> gets two commits; the bare origin is cloned from the working copy.
    /// </summary>
    /// <param name="pipelineLua">The definition committed to <c>main</c>.</param>
    /// <param name="featurePipelineLua">
    /// An optional different definition committed to <c>feature</c>, so "two branches" means two
    /// branches that would really build differently - not the same file twice.
    /// </param>
    public static MilestoneRepository Create(string pipelineLua, string? featurePipelineLua = null)
    {
        var directory = new GitTestDirectory("q002");
        var environment = GitCli.Environment(directory);
        var source = directory.Combine("source");
        Directory.CreateDirectory(source);
        GitCli.MustRun(source, environment, "init", "--initial-branch", "main");

        GitFixtureRepository.CommitFile(source, environment, "README.md", "# milestone fixture\n", "one: readme");
        GitFixtureRepository.CommitFile(
            source, environment, "pipeline.lua", pipelineLua, "two: pipeline definition");
        var mainTip = GitFixtureRepository.CommitFile(
            source, environment, "docs/notes.md", "notes\n", "three: notes");

        GitCli.MustRun(source, environment, "checkout", "-b", "feature");
        GitFixtureRepository.CommitFile(source, environment, "feature.txt", "feature one\n", "feature: one");
        var featureTip = GitFixtureRepository.CommitFile(
            source, environment, "feature.txt", "feature two\n", "feature: two");
        if (featurePipelineLua is not null)
        {
            featureTip = GitFixtureRepository.CommitFile(
                source, environment, "pipeline.lua", featurePipelineLua, "feature: its own pipeline");
        }

        GitCli.MustRun(source, environment, "checkout", "main");

        var bare = directory.Combine("origin.git");
        GitCli.MustRun(directory.RootPath, environment, "clone", "--bare", source, bare);

        // The working copy pushes into the bare origin for every later commit; this one is a no-op
        // push that proves the path works before a test depends on it.
        GitCli.MustRun(source, environment, "remote", "add", "origin", bare);
        GitCli.MustRun(source, environment, "push", "origin", "main");
        GitCli.MustRun(source, environment, "push", "origin", "feature");

        return new MilestoneRepository(directory, source, bare, mainTip, featureTip);
    }

    /// <summary>Commits <paramref name="content"/> at <paramref name="path"/> on <c>main</c> and pushes it.</summary>
    public string Commit(string path, string content, string message = "update") =>
        CommitOnBranch("main", path, content, message);

    /// <summary>Deletes <paramref name="path"/> from <c>main</c>, commits and pushes (the "no pipeline file" variant).</summary>
    public string Delete(string path, string message = "delete")
    {
        GitCli.MustRun(SourcePath, Environment, "rm", path);
        GitCli.MustRun(SourcePath, Environment, "commit", "-m", message);
        GitCli.MustRun(SourcePath, Environment, "push", "origin", "main");
        MainTip = GitCli.RevParse(SourcePath, Environment, "HEAD");
        return MainTip;
    }

    /// <summary>Replaces the repository's <c>pipeline.lua</c> on <c>main</c> and pushes it.</summary>
    public string CommitPipeline(string pipelineLua, string message = "update pipeline") =>
        Commit("pipeline.lua", pipelineLua, message);

    /// <summary>The commit a whole-file replacement of <c>pipeline.lua</c> is pushed as.</summary>
    public string CommitPipelineOnBranch(string branch, string pipelineLua)
    {
        var onBranch = GitCli.Run(SourcePath, Environment, "rev-parse", "--verify", branch);
        if (onBranch.ExitCode != 0)
        {
            GitCli.MustRun(SourcePath, Environment, "checkout", "-b", branch);
        }
        else
        {
            GitCli.MustRun(SourcePath, Environment, "checkout", branch);
        }

        var commit = GitFixtureRepository.CommitFile(
            SourcePath, Environment, "pipeline.lua", pipelineLua, $"pipeline on {branch}");
        GitCli.MustRun(SourcePath, Environment, "push", "origin", branch);
        GitCli.MustRun(SourcePath, Environment, "checkout", "main");
        if (string.Equals(branch, "feature", StringComparison.Ordinal))
        {
            FeatureTip = commit;
        }

        return commit;
    }

    private string CommitOnBranch(string branch, string path, string content, string message)
    {
        if (!string.Equals(branch, "main", StringComparison.Ordinal))
        {
            throw new ArgumentException("only main is committed by this helper; use CommitPipelineOnBranch.", nameof(branch));
        }

        var commit = GitFixtureRepository.CommitFile(SourcePath, Environment, path, content, message);
        GitCli.MustRun(SourcePath, Environment, "push", "origin", "main");
        MainTip = commit;
        return commit;
    }

    /// <summary>The commit <c>main</c> really points at in the bare origin (an independent read).</summary>
    public string OriginMainTip => GitCli.RevParse(BarePath, Environment, "main");

    /// <summary>The commit <c>feature</c> really points at in the bare origin.</summary>
    public string OriginFeatureTip => GitCli.RevParse(BarePath, Environment, "feature");

    /// <summary>Reads a file out of <c>main</c> in the bare origin (no working tree there).</summary>
    public string ShowMainFile(string relativePath) =>
        GitCli.MustRun(BarePath, Environment, "show", $"{MainTip}:{relativePath}");

    public void Dispose() => _directory.Dispose();
}

/// <summary>
/// The <c>pipeline.lua</c> shapes Q-002 needs. Steps are real child processes (the ArgvEcho asset run
/// through the dotnet host, F-007's pattern): nothing here fakes an execution, so "the step ran" is a
/// process that really ran and "nothing ran" is a marker file that really is absent.
/// </summary>
internal static class MilestonePipelines
{
    /// <summary>The pipeline-level environment variable the happy path proves reaches the child.</summary>
    public const string GreetingVariable = "ZK_GREETING";

    /// <summary>The value of <see cref="GreetingVariable"/>.</summary>
    public const string Greeting = "hello-milestone";

    /// <summary>The organization secret the fixtures reference through <c>secret()</c>.</summary>
    public const string SecretName = "ZK_TOKEN";

    /// <summary>What the happy path's artifact holds, byte for byte (the probe adds one newline).</summary>
    public const string ArtifactText = "artifact-body-42";

    /// <summary>The artifact's bytes as the file system will hold them.</summary>
    public static byte[] ArtifactBytes => System.Text.Encoding.UTF8.GetBytes(ArtifactText + "\n");

    /// <summary>A step whose argv is <paramref name="argv"/> (each element a string, escaped for Lua).</summary>
    public static string Step(string name, params string[] argv)
    {
        var arguments = string.Join(", ", argv.Select(argument => $"\"{PipelineTestData.LuaString(argument)}\""));
        return $"{{ name = \"{name}\", run = {{ {arguments} }} }}";
    }

    /// <summary>
    /// A step whose own environment carries <paramref name="envLua"/> (for example a
    /// <c>${secret:NAME}</c> reference): the deferred-reference path a child really sees as a variable.
    /// </summary>
    public static string StepWithEnvironment(string name, string envLua, params string[] argv)
    {
        var arguments = string.Join(", ", argv.Select(argument => "\"" + PipelineTestData.LuaString(argument) + "\""));
        return $"{{ name = \"{name}\", env = {{ {envLua} }}, run = {{ {arguments} }} }}";
    }

    /// <summary>A step that also declares an artifact path (F-016's declarative spelling).</summary>
    public static string StepWithArtifact(string name, string artifactPath, params string[] argv) =>
        $"{{ name = \"{name}\", artifacts = {{ \"{PipelineTestData.LuaString(artifactPath)}\" }}, "
        + $"run = {{ {string.Join(", ", argv.Select(argument => $"\"{PipelineTestData.LuaString(argument)}\""))} }} }}";

    /// <summary>
    /// A complete definition over already-rendered steps. <paramref name="env"/> is required (pass
    /// <c>null</c> for none) so a step list can never be mistaken for the environment map.
    /// </summary>
    public static string Pipeline(string name, string? env, params string[] steps) => env is null
        ? $$"""
            pipeline {
              name = "{{name}}",
              steps = {
            {{string.Join("\n", steps.Select(step => "    " + step + ","))}}
              },
            }
            """
        : $$"""
            pipeline {
              name = "{{name}}",
              env = { {{env}} },
              steps = {
            {{string.Join("\n", steps.Select(step => "    " + step + ","))}}
              },
            }
            """;

    /// <summary>The dotnet host, as a single argv element.</summary>
    private static string DotNet => PipelineTestData.DotNet;

    /// <summary>The ArgvEcho asset, as a single argv element.</summary>
    private static string ArgvEcho => PipelineTestData.ArgvEcho;

    /// <summary>A step that runs ArgvEcho with <paramref name="modeAndArgs"/>.</summary>
    private static string EchoStep(string name, params string[] modeAndArgs) =>
        Step(name, [DotNet, ArgvEcho, .. modeAndArgs]);

    private static string EchoStepWithArtifact(string name, string artifactPath, params string[] modeAndArgs) =>
        StepWithArtifact(name, artifactPath, [DotNet, ArgvEcho, .. modeAndArgs]);

    private static string EchoStepWithEnvironment(string name, string envLua, params string[] modeAndArgs) =>
        StepWithEnvironment(name, envLua, [DotNet, ArgvEcho, .. modeAndArgs]);

    /// <summary>
    /// The happy-path definition the card describes: <b>three steps</b>, a pipeline-level <b>env var</b>,
    /// a <b>secret claim</b> and an <b>artifact</b>. Step 1 prints the env var, step 2 prints the secret
    /// (which must come back redacted), and step 3 writes and publishes the artifact.
    /// </summary>
    public static string Happy =>
        Pipeline(
            "milestone",
            $"{GreetingVariable} = \"{Greeting}\"",
            EchoStep("Restore", "env", GreetingVariable),
            EchoStep("Build", "echo", "building", "${secret:" + SecretName + "}"),
            EchoStepWithArtifact("Publish", "report.txt", "probe", "report.txt", ArtifactText));

    /// <summary>The definition committed to <c>feature</c>: a different pipeline, so "two branches" is real.</summary>
    public static string FeatureBranch =>
        Pipeline("feature-pipeline", env: null, EchoStep("FeatureOnly", "echo", "feature-branch"));

    /// <summary>
    /// The secret-hygiene definition: the same secret reaches <b>stdout</b>, <b>stderr</b> and an
    /// <b>artifact file</b>. The log side must say <c>***</c>; the artifact keeps the bytes the pipeline
    /// chose to write (Q-002's ruling: artifact bytes are never rewritten).
    /// </summary>
    public static string SecretEcho =>
        Pipeline(
            "secret-echo",
            env: null,
            EchoStepWithEnvironment(
                "Stdout",
                $"{SecretName} = \"${{secret:{SecretName}}}\"",
                "env",
                SecretName),
            EchoStep("Stderr", "write-slow", "0", "${secret:" + SecretName + "}"),
            EchoStepWithArtifact("Publish", "secret.txt", "probe", "secret.txt", "${secret:" + SecretName + "}"));

    /// <summary>
    /// A step that fails with <paramref name="exitCode"/> in position <paramref name="failingIndex"/>,
    /// with a marker file per step so "how far did it get" is a file on disk, not an inference. Steps
    /// before the failing one append their own marker; steps after it would append theirs.
    /// </summary>
    public static string FailingAt(string markerFile, int failingIndex, int exitCode)
    {
        var steps = new List<string>();
        for (var index = 1; index <= 3; index++)
        {
            var name = $"Step{index}";
            steps.Add(index == failingIndex
                ? EchoStep(name, "exit", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : EchoStep(name, "append", markerFile, name));
        }

        return Pipeline("failing-step", env: null, [.. steps]);
    }

    /// <summary>
    /// A run that must stop after its first step: step 1 holds for <paramref name="milliseconds"/>,
    /// steps 2 and 3 would each append their own marker.
    /// </summary>
    public static string CancelDuringFirstStep(string markerFile, int milliseconds) =>
        Pipeline(
            "cancel-me",
            env: null,
            EchoStep("Step1", "stream", milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            EchoStep("Step2", "append", markerFile, "Step2"),
            EchoStep("Step3", "append", markerFile, "Step3"));

    /// <summary>
    /// A definition that never returns, but that <em>did</em> declare a step which would append
    /// <paramref name="markerFile"/>. The definition phase must be abandoned at the deadline, so no spec
    /// is ever frozen and the marker stays absent.
    /// </summary>
    public static string RunawayDefinition(string markerFile) =>
        Pipeline(
            "runaway",
            env: null,
            EchoStep("Marker", "append", markerFile, "ran"),
            EchoStep("Never", "stream", "30000"))
        + "\nwhile true do end\n";

    /// <summary>
    /// A definition that does not compile (the card's <c>pipeline-syntax</c> variant). Its first step is
    /// a real marker step, so "the definition never compiled" and "the marker is absent" are two
    /// statements about the same run.
    /// </summary>
    public static string SyntaxError(string markerFile) =>
        "pipeline {\n"
        + "  name = \"broken\",\n"
        + "  steps = {\n"
        + "    " + EchoStep("Marker", "append", markerFile, "ran") + ",\n"
        + "    { name = \"Oops\", run = { \"true\"\n"
        + "  },\n"
        + "}\n";

    /// <summary>
    /// One sandbox probe: a definition that declares an ordinary marker step and then reaches for the
    /// host through <paramref name="additionalLua"/> (already Lua source, e.g. a call to
    /// <c>io.open</c>). If the sandbox holds, the definition never completes, so the marker is never
    /// written and nothing outside the repository was touched.
    /// </summary>
    public static string SandboxProbe(string markerFile, string additionalLua) =>
        Pipeline(
            "sandbox-probe",
            env: null,
            EchoStep("Marker", "append", markerFile, "ran"))
        + "\n" + additionalLua + "\n";

    /// <summary>A path as a Lua string literal's contents (escaped; no long brackets needed).</summary>
    public static string LuaString(string value) => PipelineTestData.LuaString(value);
}

/// <summary>
/// The push payload shapes the milestone suite signs and posts. GitHub's shape is the one the card names;
/// each payload deliberately carries a <c>clone_url</c> that is <b>not</b> the project's repository, so
/// "the delivery cannot change where a run clones from" is a property of what the run actually built.
/// </summary>
internal static class MilestonePushPayloads
{
    /// <summary>A 40-hex object name that is never a real commit in the fixture.</summary>
    public const string Before = "1111111111111111111111111111111111111111";

    /// <summary>A GitHub <c>push</c> body for <paramref name="branch"/> at <paramref name="after"/>.</summary>
    public static string GitHub(string branch, string after) => $$"""
        {
          "ref": "refs/heads/{{branch}}",
          "before": "{{Before}}",
          "after": "{{after}}",
          "created": false,
          "deleted": false,
          "forced": false,
          "repository": {
            "id": 1296269,
            "name": "attacker",
            "full_name": "evil/attacker",
            "clone_url": "https://evil.example/attacker.git",
            "ssh_url": "git@evil.example:evil/attacker.git"
          },
          "pusher": { "name": "someone", "email": "someone@example.test" },
          "head_commit": { "id": "{{after}}", "message": "push" }
        }
        """;
}
