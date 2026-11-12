using Xunit;
using Zenkins.Worker;

namespace Zenkins.Tests.WorkerRuntime;

/// <summary>
/// Card F-007's work directory service: the trio is created per run and idempotently so, a
/// malformed run id never touches the file system, and temp cleanup removes only <c>temp/</c>.
/// </summary>
[Trait("Category", "WorkerRuntime")]
public sealed class WorkDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"zenkins-f007-workdir-{Guid.NewGuid():N}");

    private readonly WorkDirectoryService _service;

    public WorkDirectoryTests()
    {
        Directory.CreateDirectory(_root);
        _service = new WorkDirectoryService(_root);
    }

    [Fact]
    public void Preparing_a_run_creates_the_directory_trio()
    {
        var runId = Guid.NewGuid();

        var work = _service.Prepare(runId);

        Assert.Equal(Path.Combine(_root, runId.ToString("D")), work.RootPath);
        Assert.Equal(Path.Combine(work.RootPath, "repository"), work.RepositoryPath);
        Assert.Equal(Path.Combine(work.RootPath, "artifacts"), work.ArtifactsPath);
        Assert.Equal(Path.Combine(work.RootPath, "temp"), work.TempPath);
        Assert.True(Directory.Exists(work.RepositoryPath), "repository/ was not created.");
        Assert.True(Directory.Exists(work.ArtifactsPath), "artifacts/ was not created.");
        Assert.True(Directory.Exists(work.TempPath), "temp/ was not created.");
    }

    [Fact]
    public void Preparing_the_same_run_twice_is_idempotent_and_keeps_contents()
    {
        var runId = Guid.NewGuid();
        var first = _service.Prepare(runId);
        var marker = Path.Combine(first.RepositoryPath, "keep-me.txt");
        File.WriteAllText(marker, "content");

        var second = _service.Prepare(runId.ToString());

        Assert.Equal(first.RootPath, second.RootPath);
        Assert.True(File.Exists(marker), "Re-preparing the run wiped existing contents.");
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("../../..")]
    [InlineData("not-a-guid")]
    [InlineData("12345")]
    [InlineData("")]
    [InlineData("a/b/c")]
    public void A_malformed_run_id_is_refused_and_never_touches_the_file_system(string malformedRunId)
    {
        var before = Directory.GetFileSystemEntries(_root).ToArray();

        var failure = Assert.Throws<WorkDirectoryException>(() => _service.Prepare(malformedRunId));

        Assert.Contains("not a GUID", failure.Message, StringComparison.Ordinal);
        Assert.Equal(before, Directory.GetFileSystemEntries(_root).ToArray());
    }

    [Fact]
    public void An_exotic_but_parseable_guid_spelling_maps_to_the_canonical_directory_name()
    {
        var runId = Guid.NewGuid();

        // "{...}" and "N" spellings parse; the directory name must still be the canonical D form,
        // so no input can smuggle characters into the path.
        var work = _service.Prepare($"{{{runId.ToString("D").ToUpperInvariant()}}}");

        Assert.Equal(Path.Combine(_root, runId.ToString("D")), work.RootPath);
        Assert.True(Directory.Exists(work.RepositoryPath));
    }

    [Fact]
    public void CleanupTemp_removes_only_temp()
    {
        var work = _service.Prepare(Guid.NewGuid());
        File.WriteAllText(Path.Combine(work.RepositoryPath, "repo.txt"), "r");
        File.WriteAllText(Path.Combine(work.ArtifactsPath, "artifact.txt"), "a");
        File.WriteAllText(Path.Combine(work.TempPath, "junk.log"), "t");

        _service.CleanupTemp(work);

        Assert.False(Directory.Exists(work.TempPath), "temp/ survived cleanup.");
        Assert.True(Directory.Exists(work.RepositoryPath), "repository/ was removed by temp cleanup.");
        Assert.True(Directory.Exists(work.ArtifactsPath), "artifacts/ was removed by temp cleanup.");
        Assert.True(File.Exists(Path.Combine(work.RepositoryPath, "repo.txt")));
        Assert.True(File.Exists(Path.Combine(work.ArtifactsPath, "artifact.txt")));
    }

    [Fact]
    public void CleanupTemp_on_an_already_absent_temp_is_a_no_op()
    {
        var work = _service.Prepare(Guid.NewGuid());
        Directory.Delete(work.TempPath);

        _service.CleanupTemp(work);

        Assert.False(Directory.Exists(work.TempPath));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
