using Xunit;
using Zenkins.Worker;

namespace Zenkins.Tests.Workers;

/// <summary>
/// Card F-005's token persistence: the path scheme per server host + worker name, the round trip,
/// the atomic overwrite, and - on POSIX - the 0700/0600 permission contract. Windows runs the
/// functional asserts and skips the Unix-mode ones (best-effort ACL hardening is deferred).
/// </summary>
[Trait("Category", "Workers")]
public sealed class WorkerTokenStoreTests : IDisposable
{
    private readonly string _dataDirectory;

    public WorkerTokenStoreTests() =>
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"zenkins-f005-{Guid.NewGuid():N}");

    private static Uri ServerUrl => new("http://127.0.0.1:5099");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Save_and_load_round_trip_the_worker_token()
    {
        var store = new WorkerTokenStore(_dataDirectory, ServerUrl, "round-trip-agent");
        var token = WorkerTokenStore.TokenPrefix + "abc123def";

        store.Save(token);

        Assert.Equal(token, store.Load());
    }

    [Fact]
    public void Loading_without_a_persisted_token_returns_null()
    {
        var store = new WorkerTokenStore(_dataDirectory, ServerUrl, "fresh-agent");

        Assert.Null(store.Load());
    }

    [Fact]
    public void Saving_again_overwrites_atomically_and_the_latest_token_wins()
    {
        var store = new WorkerTokenStore(_dataDirectory, ServerUrl, "rotate-agent");

        store.Save(WorkerTokenStore.TokenPrefix + "first");
        store.Save(WorkerTokenStore.TokenPrefix + "second");

        Assert.Equal(WorkerTokenStore.TokenPrefix + "second", store.Load());
        Assert.Empty(Directory.GetFiles(store.DirectoryPath, "*.tmp-*"));
    }

    [Fact]
    public void The_path_scheme_separates_server_hosts_and_worker_names()
    {
        var storeOne = new WorkerTokenStore(_dataDirectory, new Uri("http://alpha.example.test"), "agent");
        var storeTwo = new WorkerTokenStore(_dataDirectory, new Uri("http://beta.example.test"), "agent");
        var storeThree = new WorkerTokenStore(_dataDirectory, new Uri("http://alpha.example.test"), "agent-2");

        Assert.NotEqual(storeOne.StorePath, storeTwo.StorePath);
        Assert.NotEqual(storeOne.StorePath, storeThree.StorePath);
        Assert.NotEqual(storeTwo.StorePath, storeThree.StorePath);
        Assert.StartsWith(_dataDirectory, storeOne.StorePath, StringComparison.Ordinal);
        Assert.EndsWith(".token", storeOne.StorePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsafe_characters_in_host_or_name_cannot_escape_the_store_root()
    {
        var store = new WorkerTokenStore(_dataDirectory, new Uri("http://192.0.2.7:8080"), "../../escape");
        var storeRoot = Path.GetFullPath(_dataDirectory);

        // The true safety property: the resolved store path stays inside the data directory, and no
        // segment is a traversal.
        Assert.StartsWith(storeRoot, Path.GetFullPath(store.StorePath), StringComparison.Ordinal);
        Assert.All(store.StorePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            segment => Assert.NotEqual("..", segment));
    }

    [Fact]
    public void Distinct_hosts_or_names_never_map_to_one_token_file()
    {
        // Sanitized names that would collide ("a.b" vs "a-b") must not share a credential file.
        var colon = new WorkerTokenStore(_dataDirectory, new Uri("http://a.b"), "agent");
        var dash = new WorkerTokenStore(_dataDirectory, new Uri("http://a-b"), "agent");
        var otherName = new WorkerTokenStore(_dataDirectory, new Uri("http://a.b"), "agent2");

        Assert.NotEqual(colon.StorePath, dash.StorePath);
        Assert.NotEqual(colon.StorePath, otherName.StorePath);
    }

    [Fact]
    public void A_corrupt_token_file_is_reported_not_ignored()
    {
        var store = new WorkerTokenStore(_dataDirectory, ServerUrl, "corrupt-agent");
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(store.StorePath, "not-a-worker-token");

        Assert.Throws<WorkerTokenStoreException>(() => store.Load());
    }

    [Fact]
    public void An_empty_token_file_is_reported_not_ignored()
    {
        var store = new WorkerTokenStore(_dataDirectory, ServerUrl, "empty-agent");
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(store.StorePath, "   ");

        Assert.Throws<WorkerTokenStoreException>(() => store.Load());
    }

    [Fact]
    public void Saving_a_non_worker_token_value_is_refused()
    {
        var store = new WorkerTokenStore(_dataDirectory, ServerUrl, "paranoid-agent");

        Assert.Throws<WorkerTokenStoreException>(() => store.Save("zreg_not-a-worker-token"));
    }

    [Fact]
    public void The_directory_is_0700_and_the_file_is_0600_on_posix()
    {
        if (OperatingSystem.IsWindows())
        {
            // Best-effort ACL hardening on Windows is documented as deferred; the profile
            // directory is already user-private there.
            return;
        }

        var store = new WorkerTokenStore(_dataDirectory, ServerUrl, "permissions-agent");
        store.Save(WorkerTokenStore.TokenPrefix + "mode-test");

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(store.DirectoryPath));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(store.StorePath));
    }
}
