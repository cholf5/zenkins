using System.Text.Json;
using Zenkins.Server.Git;

namespace Zenkins.Server.Webhooks;

/// <summary>
/// The normalised facts of a push delivery (card F-017). One small parser instead of three
/// integrations: GitHub, Gitea and GitLab all describe a push with <c>ref</c>, <c>after</c> and
/// <c>before</c>, and only those fields decide what happens.
/// </summary>
/// <param name="Ref">The full ref the push updated (<c>refs/heads/main</c>, <c>refs/tags/v1</c>).</param>
/// <param name="After">The commit the ref points at after the push; all zeroes for a delete.</param>
/// <param name="Before">The previous value of the ref (informational; a delete's <c>before</c> is the
/// commit that disappeared).</param>
/// <param name="CloneUrl">
/// The sender's idea of the repository's clone URL. <b>Informational only</b>: it is never used to pick
/// where the run clones from, because a signature-holding sender could otherwise redirect a build to an
/// arbitrary repository. The run's repository always comes from the project.
/// </param>
public sealed record WebhookPushPayload(string Ref, string After, string? Before, string? CloneUrl);

/// <summary>How a delivery's <c>ref</c> classifies.</summary>
public enum WebhookRefKind
{
    /// <summary>A branch update (<c>refs/heads/…</c>): the only kind that can build.</summary>
    Branch = 0,

    /// <summary>A ref that is not a branch (a tag, a note): accepted and ignored, never a run.</summary>
    NotABranch = 1,

    /// <summary>A <c>refs/heads/</c> with no branch name: not a usable ref, a 400.</summary>
    Invalid = 2,
}

/// <summary>
/// The payload parser (card F-017): reads <c>ref</c>/<c>after</c>/<c>before</c> and, when present,
/// <c>repository.clone_url</c>.
/// <para>
/// <b>Deliberately not a provider integration.</b> No event type, no provider detection, no per-host
/// shape: a payload without a usable <c>ref</c> or <c>after</c> is refused, and everything else is the
/// same three fields for all three hosts.
/// </para>
/// <para>
/// <b>A tag is not a branch.</b> <c>refs/tags/v1</c> is <see cref="WebhookRefKind.NotABranch"/>: answered
/// 202-and-ignored rather than 400, because a tag push is a legitimate thing for a git host to deliver
/// and refusing it would fill the host's delivery log with red for every release.
/// </para>
/// </summary>
public static class WebhookPayloadParser
{
    /// <summary>Longest accepted <c>ref</c> (git's own limit is far smaller).</summary>
    public const int MaxRefLength = 512;

    /// <summary>Longest accepted <c>repository.clone_url</c>; over-long values are simply not read.</summary>
    public const int MaxCloneUrlLength = 2048;

    /// <summary>The branch prefix a push has to carry to be buildable.</summary>
    public const string BranchRefPrefix = "refs/heads/";

    /// <summary>
    /// Parses a push payload. Returns <see langword="false"/> (and <paramref name="errorCode"/>
    /// <c>payload-invalid</c>) for malformed JSON, a missing/blank <c>ref</c>, a missing/blank
    /// <c>after</c>, a non-hex <c>after</c>, or a payload whose root is not an object.
    /// </summary>
    public static bool TryParse(
        ReadOnlyMemory<byte> body,
        out WebhookPushPayload? payload,
        out string? errorCode)
    {
        payload = null;
        errorCode = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            errorCode = WebhookPayloadErrorCodes.PayloadInvalid;
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                errorCode = WebhookPayloadErrorCodes.PayloadInvalid;
                return false;
            }

            var root = document.RootElement;
            var refValue = ReadString(root, "ref", MaxRefLength);
            var after = ReadString(root, "after", GitCommitValidation.MaxLength);

            if (string.IsNullOrWhiteSpace(refValue) || string.IsNullOrWhiteSpace(after))
            {
                errorCode = WebhookPayloadErrorCodes.PayloadInvalid;
                return false;
            }

            // Object names are hex; git hosts send lower case, but the shape is what matters. A delete
            // event's all-zero value passes this check too - the service classifies delete-versus-build
            // before it creates anything, which keeps "zeroes" the delete marker rather than a 400.
            after = after.ToLowerInvariant();
            if (!GitCommitValidation.IsValid(after))
            {
                errorCode = WebhookPayloadErrorCodes.PayloadInvalid;
                return false;
            }

            payload = new WebhookPushPayload(
                refValue,
                after,
                ReadString(root, "before", GitCommitValidation.MaxLength)?.ToLowerInvariant(),
                ReadNestedString(root, "repository", "clone_url", MaxCloneUrlLength));
            return true;
        }
    }

    /// <summary>
    /// Classifies and extracts a branch from <c>ref</c>. Note that <c>after</c> is not consulted: a tag
    /// delete is still not a branch.
    /// </summary>
    public static WebhookRefKind ReadRef(string refValue, out string branch)
    {
        ArgumentNullException.ThrowIfNull(refValue);
        branch = string.Empty;

        if (refValue.StartsWith(BranchRefPrefix, StringComparison.Ordinal))
        {
            var candidate = refValue[BranchRefPrefix.Length..];
            if (candidate.Length == 0)
            {
                return WebhookRefKind.Invalid;
            }

            branch = candidate;
            return WebhookRefKind.Branch;
        }

        return WebhookRefKind.NotABranch;
    }

    /// <summary>
    /// Whether <paramref name="after"/> is the all-zero object name git sends for a delete event: the
    /// ref is gone, so there is nothing to build and the delivery is accepted-and-ignored.
    /// </summary>
    public static bool IsDeleteEvent(string after) =>
        after.Length is 40 or 64 && after.All(character => character == '0');

    private static string? ReadString(JsonElement element, string property, int maxLength) =>
        element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text
            && text.Length > 0
            && text.Length <= maxLength
                ? text
                : null;

    private static string? ReadNestedString(JsonElement element, string objectName, string property, int maxLength) =>
        element.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? ReadString(nested, property, maxLength)
            : null;
}

/// <summary>The one payload-side error code: the body is not a push this product can read.</summary>
public static class WebhookPayloadErrorCodes
{
    public const string PayloadInvalid = "payload-invalid";
}
