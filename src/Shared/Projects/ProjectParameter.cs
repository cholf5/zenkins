using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zenkins.Shared.Projects;

/// <summary>
/// One build parameter of a Project (card F-021, PRD §3.10): the definition a Member configures on the
/// job and the trigger form renders. Jenkins-parity in the small - a parameter has a name (the
/// environment variable the steps read), a type, an optional default and, for a choice, the allowed
/// values. Values themselves live on the run (a snapshot frozen at trigger time), never here: this
/// record is the definition, so editing it later never moves a queued run.
/// <para>
/// <b>Names are environment variables.</b> A parameter is injected into every step's environment under
/// its own name, one layer above the organization's environment variables and below the pipeline's and
/// the step's own <c>env</c> (PRD §3.11.3, card F-034), so the name must pass the reference-name shape -
/// the same rule <c>secret()</c> targets and stored secrets follow. The <c>ZENKINS_*</c> namespace belongs
/// to the server's own run metadata and is refused when a definition is saved.
/// </para>
/// </summary>
/// <param name="Name">Env-variable shaped (letter/underscore first, then letters, digits, underscores).</param>
/// <param name="Type">One of <see cref="ProjectParameterTypes"/>; decides the trigger form's editor and
/// the value rules (a boolean is <c>true</c>/<c>false</c>, a choice must be one of <see cref="Choices"/>).</param>
/// <param name="DefaultValue">The value a trigger without user input (a webhook, or an untouched form)
/// resolves to; <see langword="null"/> means the type's neutral default (empty string, or <c>false</c>
/// for a boolean). Never collected, never treated as sensitive - parameters are not secrets.</param>
/// <param name="Choices">The allowed values of a <see cref="ProjectParameterTypes.Choice"/> parameter,
/// in display order; the first choice is the neutral default.</param>
/// <param name="Description">The hint the trigger form renders under the editor; optional.</param>
public sealed record ProjectParameter(
    string Name,
    string Type,
    string? DefaultValue = null,
    string[]? Choices = null,
    string? Description = null);

/// <summary>The parameter types a Project may declare (card F-021). A closed set, like the spec's.</summary>
public static class ProjectParameterTypes
{
    /// <summary>A single-line text input; the value is passed through as-is.</summary>
    public const string String = "string";

    /// <summary>A multi-line text input - for values that are themselves text blocks.</summary>
    public const string Text = "text";

    /// <summary>A checkbox; the value is exactly <c>true</c> or <c>false</c>.</summary>
    public const string Boolean = "boolean";

    /// <summary>A dropdown whose value must be one of the declared <see cref="ProjectParameter.Choices"/>.</summary>
    public const string Choice = "choice";

    /// <summary>
    /// A value typed on purpose and never displayed again (card F-041): the run page and every list show
    /// <see cref="MaskedValue"/> instead, the worker registers the real value with its redaction filter, and
    /// no API returns it. <b>Not a stored Secret</b> - it is an input to one run, not organization state, so
    /// triggering a deploy does not need Owner rights and nothing about it outlives the run.
    /// <para>
    /// A definition may not declare a default: a default would put the password on the project row, which is
    /// exactly what "a per-run input, not a Secret" rules out. A trigger that supplies nothing (a schedule, a
    /// poll, an API call) resolves to the empty string, and the step sees that.
    /// </para>
    /// </summary>
    public const string Password = "password";

    /// <summary>
    /// A choice of one of the project's earlier runs (card F-041). The injected value is that run's
    /// <b>number</b> (the <c>#N</c> a person reads); the step also gets <c>&lt;NAME&gt;_RUN_ID</c> and
    /// <c>&lt;NAME&gt;_COMMIT</c>. A trigger without a value selects the project's most recent successful run;
    /// a project that has none is refused rather than given a run that does not exist.
    /// </summary>
    public const string Run = "run";

    /// <summary>
    /// A git ref (a tag, a branch name, a commit) the step reads as a value (card F-041). It is <b>not</b> what
    /// the checkout uses: when the trigger pins a commit (a webhook push, a rebuild) the run's own commit is
    /// authoritative and the parameter is only what the step sees.
    /// </summary>
    public const string GitRef = "gitRef";

    /// <summary>
    /// A file uploaded with the trigger (card F-041). The step reads a <b>path</b>; the bytes are stored sealed
    /// on the run and fetched by the worker executing it, so they never ride in the claim payload (card F-040's
    /// "the claim carries names only" rule). The file is bounded and lives in the run's own directory on the
    /// worker, is never served back to a browser, and disappears with the run's temp tree.
    /// </summary>
    public const string File = "file";

    /// <summary>Every type the data layer accepts; anything else is refused at save time.</summary>
    public static readonly string[] All = [String, Text, Boolean, Choice, Password, Run, GitRef, File];

    /// <summary>
    /// The value a masked parameter is stored and displayed as (card F-041). Storing the mask rather than the
    /// plaintext is what makes the run's own snapshot show <c>***</c> with no display-time special case: a page
    /// that printed the snapshot verbatim still could not print the password.
    /// </summary>
    public const string MaskedValue = "***";

    /// <summary>Whether <paramref name="type"/> is one of the known types.</summary>
    public static bool IsValid(string? type) =>
        type is not null && Array.IndexOf(All, type) >= 0;

    /// <summary>Whether a parameter of this kind is a run input that must never be shown.</summary>
    public static bool IsMasked(string? type) =>
        string.Equals(type, Password, StringComparison.Ordinal);

    /// <summary>Whether a parameter of this kind selects an earlier run and so carries the two extras.</summary>
    public static bool IsRunSelection(string? type) =>
        string.Equals(type, Run, StringComparison.Ordinal);

    /// <summary>Whether a parameter of this kind is an uploaded file the worker materialises.</summary>
    public static bool IsFile(string? type) =>
        string.Equals(type, File, StringComparison.Ordinal);
}

/// <summary>
/// The bounds of a Project's parameter definitions and values (card F-021). A parameter list is
/// hand-written configuration, not a data channel; the bounds keep the stored JSON bounded - the same
/// discipline the frozen spec's caps follow.
/// </summary>
public static class ProjectParameterLimits
{
    /// <summary>An upper bound on the parameters one Project may declare.</summary>
    public const int MaxParametersPerProject = 32;

    /// <summary>An upper bound on a parameter name (mirrors the stored-secret name bound).</summary>
    public const int MaxNameLength = 64;

    /// <summary>An upper bound on a single-line string value or a choice entry.</summary>
    public const int MaxStringValueLength = 200;

    /// <summary>An upper bound on a multi-line text value (a text parameter may carry a whole block).</summary>
    public const int MaxTextValueLength = 8_192;

    /// <summary>An upper bound on the choices one choice parameter declares.</summary>
    public const int MaxChoices = 32;

    /// <summary>An upper bound on a parameter's description.</summary>
    public const int MaxDescriptionLength = 200;

    /// <summary>
    /// An upper bound on a <c>password</c> value (card F-041). The text bound, not the single-line one: a
    /// passphrase may carry a newline, and the value is never normalised on the way in.
    /// </summary>
    public const int MaxPasswordValueLength = MaxTextValueLength;

    /// <summary>An upper bound on a <c>gitRef</c> value: a ref name, not a command line.</summary>
    public const int MaxGitRefLength = 200;

    /// <summary>
    /// An upper bound on an uploaded <c>file</c> parameter (card F-041): 1 MiB. It is a build <em>input</em> - a
    /// licence file, a config overlay - not a repository and not an artifact, and the bound is enforced twice
    /// (the request pipeline refuses a bigger body, the handler refuses a bigger file) so a caller cannot choose
    /// which check it hits.
    /// </summary>
    public const int MaxFileBytes = 1024 * 1024;

    /// <summary>
    /// The run-relative location a <c>file</c> parameter is materialised at (card F-041), under the run's own
    /// <c>temp/</c> tree so the existing cleanup owns it. This is the value the run page shows: the absolute path
    /// is the worker's, and it is the worker that puts it in the step's environment.
    /// </summary>
    public static string FileLocation(string name) => "temp/parameters/" + name;
}

/// <summary>
/// The value rules the two sides share (card F-041): the parameters editor checks a definition's default with
/// them and the trigger checks a supplied value with the same code, so a default the editor accepts is never
/// one the trigger would refuse.
/// <para>
/// They answer <b>shape</b> only. Whether a selected run exists is a database question, so it is answered where
/// the database is (the Server's trigger path), not here - this file is also compiled into the AOT Worker.
/// </para>
/// </summary>
public static class ProjectParameterRules
{
    /// <summary>
    /// Whether <paramref name="value"/> is a usable git ref (card F-041): a non-empty, bounded string with no
    /// whitespace and no leading <c>-</c>. The last two rules are the ones that matter - a ref travels as a
    /// value into argv sooner or later, and a leading dash is how a value becomes an option
    /// (<c>--upload-pack=…</c>), while whitespace is how one argument becomes two.
    /// </summary>
    public static bool IsValidGitRef(string? value, out string normalized)
    {
        normalized = value ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > ProjectParameterLimits.MaxGitRefLength)
        {
            return false;
        }

        return normalized[0] != '-' && !normalized.Any(char.IsWhiteSpace);
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a run <b>number</b> (card F-041): the <c>#N</c> a person reads, not
    /// the run's guid. Positive on purpose - <c>#0</c> is what a row without a number reads like, and no build
    /// is numbered zero.
    /// </summary>
    public static bool IsRunNumber(string? value, out int number)
    {
        number = 0;
        return value is { Length: > 0 and <= 10 }
            && int.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out number)
            && number > 0;
    }
}

/// <summary>
/// The JSON contract of the two parameter documents (card F-021): a Project's definitions
/// (<see cref="ProjectParameter"/>[]) and a run's value snapshot (a name → value object). CamelCase,
/// nulls omitted, source-generated so the AOT-constrained Worker build needs no reflection serializer
/// (the claim payload carries the snapshot).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ProjectParameter[]))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class ProjectParameterJsonContext : JsonSerializerContext;
