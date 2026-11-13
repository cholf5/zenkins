using Xunit;
using Zenkins.Tests.Git;
using Zenkins.Tests.WorkerRuntime;

namespace Zenkins.Tests.Pipelines;

/// <summary>
/// A real Git repository that declares a <c>pipeline.lua</c>, plus the bare origin the worker clones
/// (card F-014). The working copy is where new commits (and new pipeline definitions) are made; the
/// bare repository is what <c>git clone</c> reads, so "the branch moved" is a real push, not a mocked
/// value.
/// <para>
/// The project rows the tests create point at <see cref="FileUrl"/> - a real transport, so F-013's
/// clone runs unchanged. The project form refuses <c>file://</c> (a server-side read of the server's
/// disk), which is why a fixture seeds the row directly: that rule protects a product path, and a test
/// needs a repository it can actually reach.
/// </para>
/// </summary>
internal sealed class PipelineRepository : IDisposable
{
    private readonly GitFixtureRepository _repository;

    private PipelineRepository(GitFixtureRepository repository) => _repository = repository;

    /// <summary>The <c>file://</c> URL the worker clones.</summary>
    public string FileUrl => _repository.FileUrl;

    /// <summary>The bare origin's path (what a commit is pushed into).</summary>
    public string BarePath => _repository.BarePath;

    /// <summary>The commit at the tip of <c>main</c> after the last push.</summary>
    public string MainTip { get; private set; } = string.Empty;

    /// <summary>Creates a fixture whose <c>main</c> already contains <paramref name="pipelineLua"/>.</summary>
    public static PipelineRepository Create(string pipelineLua, string pipelinePath = "pipeline.lua")
    {
        var repository = GitFixtureRepository.Create("pipeline");
        var fixture = new PipelineRepository(repository)
        {
            MainTip = repository.MainTip,
        };
        GitCli.MustRun(
            repository.SourcePath,
            repository.Environment,
            "remote", "add", "origin", repository.BarePath);
        fixture.CommitPipeline(pipelineLua, pipelinePath, "pipeline definition");
        return fixture;
    }

    /// <summary>Commits a pipeline definition into the working copy and pushes it to the origin.</summary>
    public string CommitPipeline(
        string pipelineLua,
        string pipelinePath = "pipeline.lua",
        string message = "update pipeline") =>
        Commit(pipelinePath, pipelineLua, message);

    /// <summary>
    /// Commits a pipeline definition on <paramref name="branch"/> (creating it if needed) and pushes
    /// it: the branch-selection case - two branches, two different step lists.
    /// </summary>
    public string CommitPipelineOnBranch(string branch, string pipelineLua)
    {
        var environment = _repository.Environment;
        var existing = GitCli.Run(_repository.SourcePath, environment, "rev-parse", "--verify", branch);
        if (existing.ExitCode != 0)
        {
            GitCli.MustRun(_repository.SourcePath, environment, "checkout", "-b", branch);
        }
        else
        {
            GitCli.MustRun(_repository.SourcePath, environment, "checkout", branch);
        }

        var commit = GitFixtureRepository.CommitFile(
            _repository.SourcePath, environment, "pipeline.lua", pipelineLua, "branch definition");
        GitCli.MustRun(_repository.SourcePath, environment, "push", "origin", branch);
        GitCli.MustRun(_repository.SourcePath, environment, "checkout", "main");
        return commit;
    }

    /// <summary>Commits an arbitrary file into the repository and pushes it (TC-004's "the branch moved").</summary>
    public string Commit(string relativePath, string content, string message = "commit")
    {
        var environment = _repository.Environment;
        var commit = GitFixtureRepository.CommitFile(
            _repository.SourcePath, environment, relativePath, content, message);
        GitCli.MustRun(_repository.SourcePath, environment, "push", "origin", "main");
        MainTip = commit;
        return commit;
    }

    /// <summary>Reads a file out of <c>main</c> in the bare origin (no working tree there).</summary>
    public string ShowMainFile(string relativePath) =>
        GitCli.MustRun(_repository.BarePath, _repository.Environment, "show", $"main:{relativePath}");

    public void Dispose() => _repository.Dispose();
}

/// <summary>Shared plumbing for card F-014's pipeline tests.</summary>
internal static class PipelineTestData
{
    /// <summary>The absolute path of the dotnet host, as a Lua-safe literal (forward slashes).</summary>
    public static string DotNet => ArgvEchoBinary.DotNetPath.Replace('\\', '/');

    /// <summary>The absolute path of the ArgvEcho asset, as a Lua-safe literal.</summary>
    public static string ArgvEcho => ArgvEchoBinary.DllPath.Replace('\\', '/');

    /// <summary>
    /// One step that runs ArgvEcho with <paramref name="modeAndArgs"/>. Paths use a Lua long string
    /// (<c>[[…]]</c>) so a Windows path's backslashes are never read as escapes.
    /// </summary>
    public static string Step(string name, params string[] modeAndArgs)
    {
        var arguments = string.Join(
            ", ",
            new[] { $"[[{DotNet}]]", $"[[{ArgvEcho}]]" }.Concat(
                modeAndArgs.Select(argument => $"\"{LuaString(argument)}\"")));
        return $"{{ name = \"{name}\", run = {{ {arguments} }} }}";
    }

    /// <summary>
    /// One argument as the inside of a Lua string literal: backslash and quote escaped, and a newline
    /// spelled <c>\n</c> (a literal newline is not legal inside a Lua string). Used where a step has to
    /// carry a whole file's content as one argument.
    /// </summary>
    public static string LuaString(string value) => value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r", string.Empty)
        .Replace("\n", "\\n");

    /// <summary>A complete definition over <paramref name="steps"/> (already rendered by <see cref="Step"/>).</summary>
    public static string Pipeline(string name, params string[] steps) => $$"""
        pipeline {
          name = "{{name}}",
          steps = {
        {{string.Join("\n", steps.Select(step => "    " + step + ","))}}
          },
        }
        """;

    /// <summary>
    /// A pipeline whose steps are file markers - the shape TC-001/TC-002 need: each step appends its
    /// own name to one marker file, so the file itself records the order the steps really ran in.
    /// </summary>
    public static string MarkerPipeline(string markerFile) =>
        Pipeline(
            "markers",
            Step("Restore", "append", markerFile, "restore"),
            Step("Build", "append", markerFile, "build"),
            Step("Package", "append", markerFile, "package"));

}
