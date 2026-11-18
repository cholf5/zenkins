namespace Zenkins.Shared.Pipelines;

/// <summary>
/// The names the server itself owns in a step's environment (cards F-007/F-015/F-021/F-034), and the one
/// place the reserved-name rule is written down.
/// <para>
/// <b>One rule, two callers, deliberately in Shared.</b> The server applies it where a person can still be
/// told: saving an organization variable or a build parameter inside the prefix is refused with a field
/// error naming the collision. The worker applies it where nothing can be refused (Lua has no save step):
/// <c>StepEnvironment.Compose</c> drops the name and reports it into the run log. Both sides used to carry
/// their own copy of the predicate, and the copies drifted - the server's compared
/// <see cref="StringComparison.OrdinalIgnoreCase"/>, the worker's <see cref="StringComparison.Ordinal"/> - so
/// on Windows a step env's <c>zenkins_run_id</c> was kept, written into the child's case-insensitive
/// environment block and <em>overwrote</em> the run's own <c>ZENKINS_RUN_ID</c>. One implementation is the
/// fix: the two sides cannot disagree about which names are the server's.
/// </para>
/// <para>
/// <b>The comparison is case-insensitive because an environment variable name is.</b> On Windows
/// <c>zenkins_run_id</c> and <c>ZENKINS_RUN_ID</c> are the same variable to a child process; refusing or
/// dropping only the upper-case spelling would leave the shadowing reachable under another casing.
/// </para>
/// <para>
/// It lives in <c>Zenkins.Shared</c> because both sides must agree and the Worker is the NativeAOT
/// artifact: a string prefix comparison over a static table is trimming- and AOT-safe (no reflection, no
/// culture data), which is why this is not a <c>[GeneratedRegex]</c> or a resource.
/// </para>
/// </summary>
public static class ServerEnvironmentNames
{
    /// <summary>The prefixes the server-provided metadata occupies; any name starting with one is reserved.</summary>
    public static readonly IReadOnlyList<string> ReservedPrefixes = ["ZENKINS_"];

    /// <summary>Whether <paramref name="name"/> is inside a reserved prefix.</summary>
    public static bool IsReserved(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (var prefix in ReservedPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
