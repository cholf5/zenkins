namespace Zenkins.Worker.Pipelines;

using Zenkins.Worker.Git;

/// <summary>
/// Registers a run's <b>password-typed parameter values</b> with its redaction filter (card F-041).
/// <para>
/// <b>One function, two callers.</b> The run loop calls it before the runner exists; the end-to-end test harness
/// calls the same function, so a fact about "a step that echoes the password logs the mask" is a fact about the
/// product's registration rather than about a copy of it in a test.
/// </para>
/// <para>
/// The claim names which values are passwords and the values themselves travel in the same map the environment
/// is composed from. Nothing here decides what a password is: the server's own record does, and a claim that
/// names nothing (an older server, or a run without password parameters) registers nothing.
/// </para>
/// </summary>
public static class RunParameterPasswords
{
    /// <summary>Registers every value the claim marks as a password. Safe to call with an empty claim.</summary>
    public static void Register(WorkerClaimResponse? claim, GitLogFilter redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        if (claim?.PasswordParameters is not { Count: > 0 } names || claim.Parameters is not { } values)
        {
            return;
        }

        foreach (var name in names)
        {
            if (values.TryGetValue(name, out var password))
            {
                redactor.Register(password);
            }
        }
    }
}
