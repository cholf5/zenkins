namespace Zenkins.Tests;

/// <summary>
/// Scratch-tree cleanup shared by every fixture and E2E that owns temporary directories.
/// <para>
/// Q-011's contract (a failed delete used to abandon the root silently, which is how the zenkins-*
/// roots accumulated in the system temp) extended to the whole test project. Deleting a tree that
/// holds a git repository on Windows always meets read-only files - git writes pack, idx and loose
/// object files 0444, and <see cref="Directory.Delete(string, bool)"/> reports them as
/// UnauthorizedAccessException there, where POSIX deletion does not care about the file mode. So the
/// deletion clears the read-only attribute first on Windows and retries briefly (an antivirus scan or
/// a connection still in the SQLite pool can hold a file a moment longer than the fixture expects);
/// a tree that survives every attempt is reported with one warning line - never thrown, so a leak in
/// a failing test cannot mask the real failure, and never silent.
/// </summary>
internal static class TestCleanup
{
    /// <summary>
    /// Deletes <paramref name="path"/> if it exists, with the whole-repo cleanup semantics above.
    /// Returns <c>true</c> when the tree is gone (or never was) and <c>false</c> when it survived -
    /// the survival is also reported on stderr, so callers may simply discard the result.
    /// </summary>
    public static bool DeleteTree(string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return true;
                }

                if (OperatingSystem.IsWindows())
                {
                    ClearReadOnlyAttributes(path);
                }

                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                if (attempt == 9)
                {
                    Console.Error.WriteLine(
                        $"zenkins test cleanup: '{path}' survived 10 deletion attempts "
                        + $"({failure.GetType().Name}: {failure.Message}) - left behind in the system temp.");
                    return false;
                }

                Thread.Sleep(200);
            }
        }

        return false;
    }

    /// <summary>Clears the read-only attribute on every file and directory under <paramref name="root"/>.</summary>
    private static void ClearReadOnlyAttributes(string root)
    {
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
    }
}

/// <summary>
/// Reads a file another process in the test may hold open for writing - a raw-material audit that
/// scans the live SQLite database (or its WAL) while the server under test is still running.
/// <see cref="File.ReadAllBytesAsync(string, CancellationToken)"/> opens with
/// <c>FileShare.Read</c>, which the writer's write handle makes fail on Windows ("being used by
/// another process"); POSIX has no share rules, which is why such a scan only ever fails there.
/// Opening with <c>FileShare.ReadWrite | Delete</c> coexists with the writer everywhere.
/// </summary>
internal static class TestFileRead
{
    public static async Task<byte[]> ReadHeldBytesAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        return memory.ToArray();
    }
}
