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
/// its own name (the same layer the server-provided <c>ZENKINS_*</c> variables occupy), so the name must
/// pass the reference-name shape - the same rule <c>secret()</c> targets and stored secrets follow.
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

    /// <summary>Every type the data layer accepts; anything else is refused at save time.</summary>
    public static readonly string[] All = [String, Text, Boolean, Choice];

    /// <summary>Whether <paramref name="type"/> is one of the known types.</summary>
    public static bool IsValid(string? type) =>
        type is not null && Array.IndexOf(All, type) >= 0;
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
