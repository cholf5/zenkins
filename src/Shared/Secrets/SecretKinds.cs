namespace Zenkins.Shared.Secrets;

/// <summary>
/// The one place the size of a <b>file</b> secret is decided (card F-040). Shared, because the Server refuses
/// an over-size upload and the Worker refuses to write an over-size materialisation: two copies of the number
/// would mean a stored secret the worker then cannot deliver.
/// <para>
/// Bytes, not characters - the Worker writes bytes to disk, so that is the quantity a file system cares about.
/// 64 KiB is the card's bound: big enough for a key, a certificate bundle or a <c>.p12</c>.
/// </para>
/// </summary>
public static class SecretFileLimits
{
    /// <summary>The largest file secret, in UTF-8 bytes.</summary>
    public const int MaxBytes = 64 * 1024;
}

/// <summary>
/// The kinds a stored secret can have (card F-040). The kind is what decides which reference syntax may read
/// it and which payload the ciphertext holds - it is never inferred from the payload, because an empty
/// password or a file whose content happens to look like a name must not change what a secret <em>is</em>.
/// </summary>
public static class SecretKinds
{
    /// <summary>One string value, referenced as <c>${secret:NAME}</c>. Today's behaviour, unchanged.</summary>
    public const string Text = "text";

    /// <summary>
    /// A username and a password, referenced as <c>${secret:NAME.username}</c> and
    /// <c>${secret:NAME.password}</c> - each half resolvable on its own, because <c>docker login</c> wants one
    /// in an argument and the other on stdin.
    /// </summary>
    public const string UsernamePassword = "usernamePassword";

    /// <summary>
    /// A file's contents, referenced as <c>${secretFile:NAME}</c>: the step receives a <b>path</b>, and the
    /// worker writes the material into that run's own directory.
    /// </summary>
    public const string File = "file";

    /// <summary>Every kind, in the order the form offers them.</summary>
    public static IReadOnlyList<string> All { get; } = [Text, UsernamePassword, File];

    /// <summary>Whether <paramref name="kind"/> is one of <see cref="All"/>.</summary>
    public static bool IsKnown(string? kind) =>
        kind is not null && All.Contains(kind, StringComparer.Ordinal);

    /// <summary>The kind a row without one means: everything written before card F-040 is text.</summary>
    public const string Default = Text;

    /// <summary>
    /// The column bound (<c>Secrets.Kind</c>). Roomier than today's longest kind on purpose: the bound is a
    /// storage limit, not a second copy of <see cref="All"/>, so a later kind does not need a migration.
    /// </summary>
    public const int MaxKindLength = 32;
}

/// <summary>
/// What a secret's ciphertext holds, per kind (card F-040). One envelope version: the Data Protection
/// ciphertext wraps one JSON document, and this type is the only reader and writer of its shape.
/// <para>
/// <b>Why JSON inside the envelope and not columns.</b> The pair's two halves must be encrypted together -
/// a username alone is harmless, but keeping the password's row-shape separate invites a future query that
/// reads one without the other. A single payload also gives the "one envelope version" the card asks for:
/// old rows decrypt to <c>{"value": …}</c> because that is what this type returns for a bare string.
/// </para>
/// </summary>
public sealed record SecretPayload(string Kind, string? Value, string? Username, string? Password)
{
    /// <summary>A text secret.</summary>
    public static SecretPayload ForText(string value) => new(SecretKinds.Text, value, null, null);

    /// <summary>A username/password pair.</summary>
    public static SecretPayload ForPair(string username, string password) =>
        new(SecretKinds.UsernamePassword, null, username, password);

    /// <summary>A file secret.</summary>
    public static SecretPayload ForFile(string content) =>
        new(SecretKinds.File, null, null, null) { Content = content };

    /// <summary>The file's contents, for <see cref="SecretKinds.File"/>.</summary>
    public string? Content { get; init; }

    /// <summary>
    /// Encodes the payload for the ciphertext. Text is written as the bare string it has always been, so a
    /// row written by this version is readable by an older reader and vice versa.
    /// </summary>
    public string Encode() => Kind switch
    {
        SecretKinds.Text => Value ?? string.Empty,
        SecretKinds.UsernamePassword => PairEncoding.Write(Username, Password),
        SecretKinds.File => Content ?? string.Empty,
        _ => throw new InvalidOperationException($"Unknown secret kind '{Kind}'."),
    };

    /// <summary>
    /// Reads a decrypted plaintext back into a payload of the <b>declared</b> kind. A mismatched document is
    /// refused rather than guessed: a text secret whose plaintext happens to be JSON must still be text.
    /// </summary>
    public static SecretPayload Decode(string kind, string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        return kind switch
        {
            SecretKinds.Text => ForText(plaintext),
            SecretKinds.File => ForFile(plaintext),
            SecretKinds.UsernamePassword => DecodePair(plaintext),
            _ => throw new InvalidOperationException($"Unknown secret kind '{kind}'."),
        };
    }

    private static SecretPayload DecodePair(string plaintext) =>
        PairEncoding.TryRead(plaintext, out var username, out var password)
            ? ForPair(username, password)
            : throw new InvalidOperationException(
                "A username/password secret's plaintext is not the pair document this build writes.");

    /// <summary>
    /// The on-the-wire shape of a pair: two length-prefixed fields, <c>u&lt;len&gt;:&lt;username&gt;p&lt;len&gt;:&lt;password&gt;</c>.
    /// Length prefixes rather than a separator or JSON: a password may contain anything, and Shared is compiled
    /// for AOT (a reflection-based <c>JsonSerializer</c> call fails IL2026/IL3050 here). Only
    /// <see cref="Encode"/>/<see cref="Decode"/> define this shape.
    /// </summary>
    private static class PairEncoding
    {
        public static string Write(string? username, string? password) =>
            $"u{(username ?? string.Empty).Length}:{username}p{(password ?? string.Empty).Length}:{password}";

        public static bool TryRead(string text, out string username, out string password)
        {
            username = string.Empty;
            password = string.Empty;

            if (!TryReadField(text, 'u', ref text, out username) || !TryReadField(text, 'p', ref text, out password))
            {
                return false;
            }

            return text.Length == 0;
        }

        private static bool TryReadField(string text, char marker, ref string rest, out string value)
        {
            value = string.Empty;
            if (text.Length < 3 || text[0] != marker)
            {
                return false;
            }

            var colon = text.IndexOf(':');
            if (colon < 2 || !int.TryParse(text[1..colon], out var length) || length < 0)
            {
                return false;
            }

            var start = colon + 1;
            if (text.Length < start + length)
            {
                return false;
            }

            value = text[start..(start + length)];
            rest = text[(start + length)..];
            return true;
        }
    }
}
