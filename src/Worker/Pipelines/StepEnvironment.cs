using Zenkins.Shared.Pipelines;

namespace Zenkins.Worker.Pipelines;

/// <summary>
/// A run's frozen facts, as the executor sees them when it decides whether a step's <c>when</c> condition
/// holds (card F-015).
/// </summary>
/// <param name="Branch">The branch this run checked out.</param>
/// <param name="Platform">The worker's platform, spelled the way <c>is_linux()</c>/<c>when.platform</c> are.</param>
public sealed record StepConditionFacts(string Branch, string Platform)
{
    /// <summary>The platform name of the running host (<c>linux</c>/<c>macos</c>/<c>windows</c>/<c>unknown</c>).</summary>
    public static string CurrentPlatform =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsMacOS() ? "macos"
        : "unknown";
}

/// <summary>
/// The environment of one step (card F-015, PRD §3.3.5).
/// <para>
/// <b>The precedence is explicit and testable:</b> the worker's inherited process environment, then the
/// pipeline's <c>env</c>, then the step's <c>env</c>, then the server-provided run environment
/// (<c>ZENKINS_RUN_ID</c>, <c>ZENKINS_PROJECT_ID</c>, <c>ZENKINS_WORKER_NAME</c>, <c>ZENKINS_STEP_NAME</c>).
/// A secret value enters only where the script put the reference - it is a value the script passes
/// explicitly, not a layer that can silently override a name.
/// </para>
/// <para>
/// <b>One composition, two readers.</b> The same map is what <c>env("NAME")</c> reads (through the deferred
/// reference <see cref="StepReference.EnvironmentPrefix"/>) and what is handed to the child process - so a
/// script and its steps can never disagree about a value or about which layer won.
/// </para>
/// </summary>
public static class StepEnvironment
{
    /// <summary>The per-step metadata every step sees (it is the run's environment plus the step's name).</summary>
    public const string StepNameVariable = "ZENKINS_STEP_NAME";

    /// <summary>
    /// Composes one step's effective environment. <paramref name="inherited"/> is the worker's own process
    /// environment; a <see langword="null"/> value in one of the declared layers means "do not set"
    /// (nothing is removed: a pipeline can add variables, never unset the worker's).
    /// </summary>
    public static Dictionary<string, string?> Compose(
        IReadOnlyDictionary<string, string?> inherited,
        IReadOnlyDictionary<string, string>? pipelineEnvironment,
        IReadOnlyDictionary<string, string>? stepEnvironment,
        IReadOnlyDictionary<string, string?> runEnvironment)
    {
        ArgumentNullException.ThrowIfNull(inherited);

        var composed = new Dictionary<string, string?>(inherited, StringComparer.Ordinal);
        Apply(composed, pipelineEnvironment);
        Apply(composed, stepEnvironment);
        Apply(composed, runEnvironment);
        return composed;
    }

    /// <summary>The worker's own process environment, as the child would inherit it.</summary>
    public static Dictionary<string, string?> InheritedEnvironment()
    {
        var inherited = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && name.Length > 0)
            {
                inherited[name] = entry.Value as string;
            }
        }

        return inherited;
    }

    private static void Apply<TValue>(
        Dictionary<string, string?> target,
        IReadOnlyDictionary<string, TValue>? layer)
    {
        if (layer is null)
        {
            return;
        }

        foreach (var (name, value) in layer)
        {
            if (name.Length == 0 || value is null)
            {
                continue;
            }

            target[name] = value.ToString();
        }
    }
}

/// <summary>
/// Evaluates a step's <c>when</c> condition against the run's frozen facts (card F-015). Every declared key
/// must match; a key that is absent is not a condition.
/// <para>
/// <b>Why this is C# and not Lua.</b> The condition is declared data, not code (PRD §3.3.5's
/// "条件执行"): the definition phase already gives scripts real Lua conditionals
/// (<c>if is_linux() then steps[#steps+1] = … end</c>), so this field exists for the case a script cannot
/// express - a condition that depends on facts the definition phase does not have (the branch this run
/// checked out, the step's effective environment).
/// </para>
/// <para>
/// A skip is always explained in the run log: <paramref name="reason"/> names the clause that did not hold
/// and both values, because "skipped" without a reason is indistinguishable from a bug.
/// </para>
/// </summary>
public static class StepCondition
{
    /// <summary>Whether the condition holds, with the human-readable reason when it does not.</summary>
    public static bool IsSatisfied(
        PipelineCondition? condition,
        StepConditionFacts facts,
        IReadOnlyDictionary<string, string?> environment,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(environment);

        reason = null;
        if (condition is null || condition.IsEmpty)
        {
            return true;
        }

        if (condition.Branch is { } branch
            && !string.Equals(branch, facts.Branch, StringComparison.Ordinal))
        {
            reason = $"when.branch \"{branch}\" does not match the run's branch \"{facts.Branch}\"";
            return false;
        }

        if (condition.Platform is { } platform
            && !string.Equals(platform, facts.Platform, StringComparison.Ordinal))
        {
            reason = $"when.platform \"{platform}\" does not match the worker's platform \"{facts.Platform}\"";
            return false;
        }

        if (condition.Env is { } expected)
        {
            foreach (var (name, value) in expected)
            {
                environment.TryGetValue(name, out var actual);
                if (actual is null)
                {
                    reason = $"when.env {name} is not set (expected \"{value}\")";
                    return false;
                }

                // Compared against the value the reference-expanded environment reports, so a declared
                // reference is compared as the executor will actually hand it over.
                var resolved = StepValueResolver.ResolveEnvironment(actual, environment);
                if (!string.Equals(resolved, value, StringComparison.Ordinal))
                {
                    reason = $"when.env {name} is \"{resolved}\" (expected \"{value}\")";
                    return false;
                }
            }
        }

        return true;
    }
}
