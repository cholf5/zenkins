namespace Zenkins.Worker.Git;

/// <summary>
/// The run's redaction filter for Git credentials (card F-013, extending F-015's filter).
/// <para>
/// <b>Registered before the first git command runs.</b> Every value decrypted for the run is
/// registered here the instant it is known, so no git output line - and no worker log line written
/// through this type - can carry it verbatim even if a remote, a credential helper or a proxy echoes
/// it back. The filter is the last line of defence, not the first: the credential never enters argv
/// and never enters the environment of a logged command either.
/// </para>
/// <para>
/// <b>Why a replacement token instead of dropping the line.</b> Dropping loses the diagnostic value
/// of the surrounding text ("fatal: Authentication failed for ..." still tells the operator what
/// happened); replacing keeps the shape and removes the secret.
/// </para>
/// <para>
/// Values shorter than <see cref="MinimumRedactableLength"/> are ignored rather than registered:
/// redacting a one-character value would mangle every line without protecting anything, and the
/// server refuses to store a Git credential that short (<c>GitCredentialValidation</c>).
/// </para>
/// </summary>
public sealed class GitLogFilter
{
    /// <summary>What replaces a redacted value.</summary>
    public const string Replacement = "[redacted]";

    /// <summary>Values below this length are not registered (they would redact unrelated text).</summary>
    public const int MinimumRedactableLength = 4;

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
    /// Registers one value to redact. Idempotent, order-independent, safe to call from the thread
    /// that just decrypted the value and from the threads that stream git's output.
    /// </summary>
    public void Register(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < MinimumRedactableLength)
        {
            return;
        }

        lock (_gate)
        {
            if (!_secrets.Contains(value, StringComparer.Ordinal))
            {
                _secrets.Add(value);
                // Longest first: a token that is a superstring of another registered value must be
                // replaced as a whole, never split by the shorter match.
                _secrets.Sort((left, right) => right.Length.CompareTo(left.Length));
            }
        }
    }

    /// <summary>Registers the material's secret, if any.</summary>
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
