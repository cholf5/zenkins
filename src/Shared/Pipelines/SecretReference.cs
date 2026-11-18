namespace Zenkins.Shared.Pipelines;

using Zenkins.Shared.Secrets;

/// <summary>
/// One parsed secret reference (card F-040): which secret, which half, and whether the step wants the value or
/// a path to it. The grammar is the card's, and the rules live here rather than in the two readers (Worker
/// substitution and the Server's D-006 capture) so the two cannot disagree about what a reference means.
/// </summary>
public sealed record SecretReference(string Name, SecretPart Part, bool IsFile)
{
    /// <summary>The form recorded for the spec's <c>secretReferences</c> (D-006).</summary>
    public string Recorded => IsFile
        ? "file:" + Name
        : Part switch
        {
            SecretPart.Username => Name + ".username",
            SecretPart.Password => Name + ".password",
            _ => Name,
        };

    /// <summary>
    /// Parses the text between a secret prefix and its terminator. <paramref name="prefix"/> is the prefix the
    /// scanner matched, so <c>${secretFile:NAME}</c> and <c>${secret:NAME.password}</c> are told apart by the
    /// text that actually appeared rather than by guessing.
    /// </summary>
    public static bool TryParse(string prefix, string inner, out SecretReference reference)
    {
        reference = null!;

        var isFile = string.Equals(prefix, StepReference.SecretFilePrefix, StringComparison.Ordinal);
        var text = inner.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var separator = text.LastIndexOf(StepReference.PartSeparator);
        if (separator < 0)
        {
            if (!StepReference.IsValidName(text))
            {
                return false;
            }

            reference = new SecretReference(text, SecretPart.Whole, isFile);
            return true;
        }

        // A file reference takes no part: the whole point is that the step receives a path.
        if (isFile)
        {
            return false;
        }

        var name = text[..separator];
        var part = text[(separator + 1)..];
        if (!StepReference.IsValidName(name))
        {
            return false;
        }

        var parsed = part switch
        {
            "username" => SecretPart.Username,
            "password" => SecretPart.Password,
            _ => (SecretPart?)null,
        };
        if (parsed is null)
        {
            return false;
        }

        reference = new SecretReference(name, parsed.Value, IsFile: false);
        return true;
    }

    /// <summary>
    /// Parses the <b>recorded</b> form (card F-040, the strings D-006 stores): <c>NAME</c>,
    /// <c>NAME.username</c>, <c>NAME.password</c> or <c>file:NAME</c>. Unlike <see cref="TryParse"/> this takes
    /// one complete string rather than a prefix and its inner text, and it applies the recorded-list bound
    /// (<see cref="PipelineSpecSchema.IsSecretReferenceName"/>) because a recorded entry must name a secret that
    /// could actually be stored.
    /// <para>
    /// One grammar, two readers: the Worker's substitution parses what the source text says
    /// (<see cref="TryParse"/>), and everything that reads the recorded list - the spec's own normaliser and the
    /// Secrets page's usage index - parses the stored form here. A second parser would be a second dialect.
    /// </para>
    /// </summary>
    public static bool TryParseRecorded(string? form, out SecretReference reference)
    {
        reference = null!;
        if (string.IsNullOrEmpty(form))
        {
            return false;
        }

        if (form.StartsWith(FileRecordedPrefix, StringComparison.Ordinal))
        {
            var fileName = form[FileRecordedPrefix.Length..];
            if (!PipelineSpecSchema.IsSecretReferenceName(fileName))
            {
                return false;
            }

            reference = new SecretReference(fileName, SecretPart.Whole, IsFile: true);
            return true;
        }

        var separator = form.LastIndexOf(StepReference.PartSeparator);
        if (separator < 0)
        {
            if (!PipelineSpecSchema.IsSecretReferenceName(form))
            {
                return false;
            }

            reference = new SecretReference(form, SecretPart.Whole, IsFile: false);
            return true;
        }

        var name = form[..separator];
        if (!PipelineSpecSchema.IsSecretReferenceName(name))
        {
            return false;
        }

        var parsed = form[(separator + 1)..] switch
        {
            "username" => SecretPart.Username,
            "password" => SecretPart.Password,
            _ => (SecretPart?)null,
        };
        if (parsed is null)
        {
            return false;
        }

        reference = new SecretReference(name, parsed.Value, IsFile: false);
        return true;
    }

    /// <summary>The prefix a recorded <b>file</b> reference carries (<c>file:NAME</c>).</summary>
    public const string FileRecordedPrefix = "file:";

    /// <summary>
    /// Whether this reference may read a secret of <paramref name="kind"/>. A mismatch is a field error with
    /// the path that produced it - a text secret has no halves, and a file secret has no value to print.
    /// </summary>
    public bool IsAllowedFor(string kind) => kind switch
    {
        SecretKinds.Text => !IsFile && Part == SecretPart.Whole,
        SecretKinds.UsernamePassword => !IsFile && Part is SecretPart.Username or SecretPart.Password,
        SecretKinds.File => IsFile,
        _ => false,
    };

    /// <summary>
    /// The explanation a mismatch deserves: what the reference asked for, and what the secret actually is -
    /// both halves, because "use ${secretFile:key}" alone does not say why.
    /// </summary>
    public string MismatchMessage(string kind)
    {
        var asked = IsFile ? $"${{secretFile:{Name}}}" : $"${{secret:{Recorded}}}";
        return kind switch
        {
            SecretKinds.Text => $"'{Name}' is a text secret and reads as ${{secret:{Name}}}; {asked} is not it.",
            SecretKinds.UsernamePassword =>
                $"'{Name}' is a username/password secret; {asked} is not one of its halves "
                + $"(${{secret:{Name}.username}} or ${{secret:{Name}.password}}).",
            SecretKinds.File => $"'{Name}' is a file secret; {asked} does not ask for its path "
                + $"(${{secretFile:{Name}}}).",
            _ => $"'{Name}' has an unknown kind and cannot be referenced by {asked}.",
        };
    }
}

/// <summary>Which half of a secret a reference asks for.</summary>
public enum SecretPart
{
    /// <summary>The whole value (text secrets, and the only sensible answer before F-040's kinds).</summary>
    Whole,

    /// <summary>The username half of a pair.</summary>
    Username,

    /// <summary>The password half of a pair.</summary>
    Password,
}
