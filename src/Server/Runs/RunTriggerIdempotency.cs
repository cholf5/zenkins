using System.Security.Cryptography;
using System.Text;

namespace Zenkins.Server.Runs;

/// <summary>
/// The idempotency scope an API trigger carries (cards F-027 + B-008): the token that sent the key and
/// the key itself. The <b>project</b> and the <b>payload hash</b> are deliberately not here - the trigger
/// path resolves them (the project from the same read the 404 comes from, the hash from
/// <see cref="RunTriggerFingerprint"/>), so a key can only ever replay against the request it named.
/// </summary>
/// <param name="ApiTokenId">The token row's id - the identity a key belongs to.</param>
/// <param name="Key">The caller's <c>Idempotency-Key</c> header value, already validated and bounded.</param>
public sealed record RunTriggerIdempotency(Guid ApiTokenId, string Key);

/// <summary>
/// Card B-008: the payload half of an <c>Idempotency-Key</c>, reduced to one stable hash.
/// <para>
/// <b>What the hash covers, and why it is canonical.</b> The branch, the required tags, the timeout and
/// the parameter values - the four inputs a trigger actually decides something with - each normalized
/// the way the trigger path normalizes it: the branch and the timeout are trimmed, tags are trimmed and
/// deduplicated (they are a set to every reader), and parameters are ordered by name (a JSON object's
/// member order is not a promise). The same retried request therefore hashes to the same value on every
/// platform and process, while a changed branch, tag, timeout or parameter value does not - and that
/// difference is what turns "silently replay the old run" into a refusal.
/// </para>
/// <para>
/// <b>Tags as sent, not as accepted (card Q-017).</b> The tag list is normalized by
/// <see cref="RunValidation.ParseTagsAsSent"/>, not by the trigger path's own
/// <see cref="RunValidation.ParseTags"/>. The two differ on exactly the entries validation refuses
/// (<c>tag-invalid</c>: over-long, or carrying a control character), and the fingerprint must cover
/// those: hashing the accepted list made a resend that differs only in such a tag hash identically and
/// replay a <c>200</c>, whereas the same request with a new key is a <c>400 tag-invalid</c> - a refusal
/// silently lost. Keeping them changes nothing for a legal resend, whose tags survive both parses
/// identically (trimmed, deduplicated case-insensitively, then ordered below).
/// </para>
/// <para>
/// <b>Raw, not resolved.</b> The hash is taken from the request as sent, because the replay decision is
/// made <em>before</em> the project's gates and field validation (a replay must answer a formerly valid
/// request even if today's definitions changed, and it must win over a disabled project - see
/// <c>docs/api-v1.md</c>). Resolving first would let a changed parameter definition turn a retry into a
/// validation error instead of the run it already queued.
/// </para>
/// <para>
/// <b>Unambiguous framing.</b> Every field is written length-prefixed, so no value can impersonate a
/// field boundary: two different payloads cannot hash the same by moving a delimiter around.
/// </para>
/// </summary>
internal static class RunTriggerFingerprint
{
    /// <summary>The payload hash of one trigger request: lowercase hex SHA-256.</summary>
    public static string Of(RunTriggerInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var text = new StringBuilder();
        AppendField(text, input.Branch?.Trim() ?? string.Empty);
        AppendField(
            text,
            string.Join(
                ',',
                RunValidation.ParseTagsAsSent(input.RequiredTags)
                    .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(tag => tag, StringComparer.Ordinal)));
        AppendField(text, input.TimeoutMinutes?.Trim() ?? string.Empty);

        // The parameter count leads its block, so an empty parameter set and a set of empty names differ.
        var parameters = (input.Parameters ?? new Dictionary<string, string>())
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
        AppendField(text, parameters.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var (name, value) in parameters)
        {
            AppendField(text, name);
            AppendField(text, value);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Writes one field as <c>{length}:{value};</c> - self-delimiting, so no value can mimic a boundary.</summary>
    private static void AppendField(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append(';');
}
