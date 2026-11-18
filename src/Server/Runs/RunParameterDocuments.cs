using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zenkins.Server.Runs;

/// <summary>
/// One parameter's own type record on a run (card F-041): which of the new kinds it was, and - for a <c>run</c>
/// selection - the run's id and commit, which become the step's <c>&lt;NAME&gt;_RUN_ID</c> /
/// <c>&lt;NAME&gt;_COMMIT</c>.
/// <para>
/// The type lives on the <b>run</b>, not only on the project, so history reads as it was: a definition retyped
/// (or deleted) after the trigger cannot change how an old run's parameters are displayed or what its steps were
/// given.
/// </para>
/// </summary>
/// <param name="Type">A <see cref="Zenkins.Shared.Projects.ProjectParameterTypes"/> value.</param>
/// <param name="RunId">The selected run's id, for a <c>run</c> parameter.</param>
/// <param name="Commit">The selected run's commit, when it had one.</param>
public sealed record RunParameterDetail(string Type, string? RunId = null, string? Commit = null);

/// <summary>
/// The two parameter documents a run carries beyond its value map (card F-041): the <b>details</b> (a plain
/// name → type record) and the <b>protected</b> values (the password-typed plaintexts, sealed as one blob).
/// <para>
/// <b>Why the mask needs both.</b> <c>BuildRun.ParametersJson</c> is the value map every reader shares - page,
/// rebuild form, claim - and a password's entry there is the literal <c>***</c>, so no reader can print it by
/// accident. The real value has to reach the worker, so it lives in the protected column and is opened only when
/// the claim is built. The details column is what tells the page (and the claim) which entries those are.
/// </para>
/// <para>
/// <b>Why </b><c>null</c><b> is meaningful in both.</b> A run triggered before this card has no details and no
/// protected values; that is "every parameter is a plain string", not an error, and it is the answer the readers
/// give for a missing column.
/// </para>
/// </summary>
public static class RunParameterDocuments
{
    /// <summary>Serialises the details for the run row; an empty set stays <see langword="null"/>.</summary>
    public static string? SerializeDetails(IReadOnlyDictionary<string, RunParameterDetail>? details) =>
        details is not { Count: > 0 }
            ? null
            : JsonSerializer.Serialize(
                new Dictionary<string, RunParameterDetail>(details),
                RunParameterJsonContext.Default.DictionaryStringRunParameterDetail);

    /// <summary>The details a run row recorded; empty for a row that predates the column.</summary>
    public static IReadOnlyDictionary<string, RunParameterDetail> ReadDetails(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, RunParameterDetail>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize(
                    json,
                    RunParameterJsonContext.Default.DictionaryStringRunParameterDetail)
                ?? new Dictionary<string, RunParameterDetail>(StringComparer.Ordinal);

    /// <summary>
    /// Seals the sensitive values for the run row; an empty set stays <see langword="null"/>. One blob rather
    /// than one per value: the set is written at trigger time and read as a set, and a single ciphertext is a
    /// single thing to get right.
    /// </summary>
    public static string? SerializeProtected(
        IReadOnlyDictionary<string, string>? values,
        RunParameterProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        if (values is not { Count: > 0 })
        {
            return null;
        }

        var json = JsonSerializer.Serialize(
            new Dictionary<string, string>(values),
            RunParameterJsonContext.Default.DictionaryStringString);
        return protector.Protect(json);
    }

    /// <summary>
    /// Opens the sensitive values of a run row. A row that predates the column (or a run whose parameters were all
    /// plain) answers an empty map; a ciphertext this process cannot open throws, because that is a key-ring fault
    /// and pretending the value is absent would queue a run with an empty password.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadProtected(
        string? ciphertext,
        RunParameterProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        if (string.IsNullOrEmpty(ciphertext))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var json = protector.Unprotect(ciphertext);
        return JsonSerializer.Deserialize(json, RunParameterJsonContext.Default.DictionaryStringString)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}

/// <summary>
/// The JSON contract of the run's parameter file document (card F-041): a name → base64 object, sealed whole
/// with <see cref="RunParameterProtector"/> - the same purpose as the password values, because both are "the
/// material a run's input consists of", and one cipher for it is one thing to back up.
/// </summary>
public static class RunParameterFiles
{
    /// <summary>Seals the uploaded files for the run row; an empty set stays <see langword="null"/>.</summary>
    public static string? Serialize(
        IReadOnlyDictionary<string, byte[]>? files,
        RunParameterProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        if (files is not { Count: > 0 })
        {
            return null;
        }

        var encoded = files.ToDictionary(
            entry => entry.Key,
            entry => Convert.ToBase64String(entry.Value),
            StringComparer.Ordinal);
        return protector.Protect(JsonSerializer.Serialize(
            encoded,
            RunParameterJsonContext.Default.DictionaryStringString));
    }

    /// <summary>
    /// Every uploaded file of a run, opened - a rebuild's way to carry the original's inputs forward without the
    /// caller ever holding a byte array it did not seal itself.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> ReadAll(
        string? ciphertext,
        RunParameterProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        if (string.IsNullOrEmpty(ciphertext))
        {
            return new Dictionary<string, byte[]>(StringComparer.Ordinal);
        }

        var json = protector.Unprotect(ciphertext);
        var encoded = JsonSerializer.Deserialize(json, RunParameterJsonContext.Default.DictionaryStringString);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (name, value) in encoded ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            try
            {
                files[name] = Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                // A corrupted entry is not a run input; skipping it leaves the parameter empty, which the
                // trigger's own rules can see (the display value is empty for a parameter with no bytes).
            }
        }

        return files;
    }

    /// <summary>
    /// Opens one uploaded file by parameter name, or <see langword="null"/> when this run holds no bytes for it
    /// (a headless trigger that uploaded none). The whole document is opened and one entry is taken: the map is
    /// small (bounded by the 32-parameter cap) and opening it per request is the honest cost of a name-scoped
    /// route.
    /// </summary>
    public static byte[]? Read(string? ciphertext, string name, RunParameterProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        if (string.IsNullOrEmpty(ciphertext) || string.IsNullOrEmpty(name))
        {
            return null;
        }

        var json = protector.Unprotect(ciphertext);
        var encoded = JsonSerializer.Deserialize(json, RunParameterJsonContext.Default.DictionaryStringString);
        if (encoded is null || !encoded.TryGetValue(name, out var value))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// The JSON contract of the run's parameter documents (card F-041). Source-generated, so the Server keeps the
/// same trimming-friendly discipline as the rest of the codebase, and camelCase to match the existing parameter
/// documents on the wire.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(Dictionary<string, RunParameterDetail>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class RunParameterJsonContext : JsonSerializerContext;
