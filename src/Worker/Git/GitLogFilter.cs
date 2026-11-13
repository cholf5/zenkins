namespace Zenkins.Worker.Git;

/// <summary>
/// One run's redaction filter (card F-013, extended by F-015): every value the worker decrypts for the
/// run - the Git credential and every <c>secret()</c> value - is registered here, and every log line the
/// run emits is passed through <see cref="Redact"/> before it leaves the worker.
/// <para>
/// <b>Registered the instant a value is known.</b> The Git credential is registered before the first git
/// command runs (F-013); a secret is registered at the moment it is decrypted, before the step that
/// referenced it starts. From then on no line - a child's stdout or stderr, a line the worker synthesized
/// itself (a step header, an exit-code line), a definition-phase <c>print</c> - can carry it verbatim.
/// </para>
/// <para>
/// <b>This is log hygiene, not a boundary.</b> PRD §3.8 says so and the docs repeat it: the worker holds
/// each plaintext in memory for the duration of the run, whoever can edit <c>pipeline.lua</c> is treated as
/// having the build's permissions, and a value that is encoded, split across two writes (no newline) or
/// otherwise transformed is <b>not</b> caught. Docs: <c>docs/lua-runtime.md</c> §7 lists the known gaps
/// rather than pretending there are none.
/// </para>
/// <para>
/// <b>Every value, however short.</b> There is no minimum length: a length threshold would be a bypass
/// (a 1-3 character secret is exactly the case an attacker would choose), and organization secrets have
/// no minimum length of their own. The cost is an occasional false positive in a log line - the safer
/// direction, and the SDK docs advise against 1-3 character secrets.
/// </para>
/// <para>
/// <b>Why a replacement token instead of dropping the line.</b> Dropping loses the diagnostic value of the
/// surrounding text ("fatal: Authentication failed for ..." still tells the operator what happened);
/// replacing keeps the shape and removes the value. One token (<c>***</c>) everywhere, so a reader never
/// has to wonder whether two different markers mean two different things.
/// </para>
/// </summary>
public sealed class GitLogFilter
{
    /// <summary>What replaces a redacted value.</summary>
    public const string Replacement = "***";

    private readonly object _gate = new();
    private readonly List<string> _secrets = [];

    /// <summary>How many distinct values are registered (asserted by the secrecy tests).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _secrets.Count;
            }
        }
    }

    /// <summary>
    /// Registers one value to redact. Idempotent, order-independent, safe to call from the thread that
    /// just decrypted the value and from the threads that stream a child's output. An empty or
    /// whitespace-only value is ignored (it would match every line and redact the whole log without
    /// protecting anything); everything else - however short - is registered.
    /// </summary>
    public void Register(string? value)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        lock (_gate)
        {
            if (!_secrets.Contains(value, StringComparer.Ordinal))
            {
                _secrets.Add(value);

                // Longest first: a value that is a superstring of another registered value must be
                // replaced as a whole, never split by the shorter match.
                _secrets.Sort((left, right) => right.Length.CompareTo(left.Length));
            }
        }
    }

    /// <summary>Registers the material's secret, if any (the F-013 path).</summary>
    public void Register(GitCredentialMaterial? material) => Register(material?.Secret);

    /// <summary>Returns <paramref name="text"/> with every registered value replaced.</summary>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        string[] secrets;
        lock (_gate)
        {
            if (_secrets.Count == 0)
            {
                return text;
            }

            secrets = [.. _secrets];
        }

        var redacted = text;
        foreach (var secret in secrets)
        {
            redacted = redacted.Replace(secret, Replacement, StringComparison.Ordinal);
        }

        return redacted;
    }

    /// <summary>Whether <paramref name="text"/> still contains a registered value.</summary>
    public bool ContainsSecret(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        lock (_gate)
        {
            return _secrets.Exists(secret => text.Contains(secret, StringComparison.Ordinal));
        }
    }
}
