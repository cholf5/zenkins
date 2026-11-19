using System.Text.RegularExpressions;
using Zenkins.Server.Localization;
using Zenkins.Shared.Pipelines;
using Zenkins.Shared.Projects;

namespace Zenkins.Server.Projects;

/// <summary>
/// Field names as the project form and the service refer to them. Kept as constants so the
/// endpoint, the pages and the tests cannot drift apart on spelling.
/// </summary>
public static class ProjectFields
{
    public const string Name = "Name";
    public const string Kind = "Kind";

    /// <summary>F-023: the branch-discovery selector (repository Pipeline projects only).</summary>
    public const string BranchDiscovery = "BranchDiscovery";

    /// <summary>F-022: the create form's definition-source selector (Pipeline kind only).</summary>
    public const string PipelineSource = "PipelineSource";

    /// <summary>F-022: the Pipelines tab's inline script editor.</summary>
    public const string InlineScript = "InlineScript";
    public const string RepositoryUrl = "RepositoryUrl";
    public const string DefaultBranch = "DefaultBranch";
    public const string PipelinePath = "PipelinePath";
    public const string Steps = "Steps";
    public const string Parameters = "Parameters";

    /// <summary>F-028: the disable form's reason (the only field of that POST).</summary>
    public const string DisabledReason = "Reason";

    /// <summary>F-042: the create form's parent-folder selector (and the move form's target).</summary>
    public const string Folder = "FolderId";

    /// <summary>F-029: the three queue controls of the limits form.</summary>
    public const string MaxConcurrentRuns = "MaxConcurrentRuns";
    public const string QuietPeriodSeconds = "QuietPeriodSeconds";
    public const string MaxQueuedRuns = "MaxQueuedRuns";
}

/// <summary>
/// Stable validation codes. The service returns the code, the endpoint puts it in the redirect
/// query and the form page maps it back to the field and the message (the same PRG shape F-002
/// uses for organization validation).
/// </summary>
public static class ProjectErrorCodes
{
    public const string NameRequired = "name-required";
    public const string NameTooLong = "name-too-long";
    public const string NameInvalid = "name-invalid";
    public const string NameDuplicate = "name-duplicate";

    /// <summary>F-042: the posted folder id is not a folder id (never coerced to the root).</summary>
    public const string FolderInvalid = "folder-invalid";

    /// <summary>F-042: the posted folder id is not a folder of the active organization.</summary>
    public const string FolderNotFound = "folder-not-found";

    /// <summary>F-020: the kind selector's value is not one of the known kinds.</summary>
    public const string KindInvalid = "kind-invalid";

    /// <summary>F-022: the definition source is not one of the known sources.</summary>
    public const string PipelineSourceInvalid = "pipeline-source-invalid";

    /// <summary>F-023: the branch-discovery mode is not one of the known modes.</summary>
    public const string BranchDiscoveryInvalid = "branch-discovery-invalid";

    /// <summary>F-023: the project has no repository (Freestyle or inline), so it cannot discover branches.</summary>
    public const string BranchDiscoveryUnavailable = "branch-discovery-unavailable";

    /// <summary>F-022: an inline Pipeline needs a Lua body - there is no repository file to fall back on.</summary>
    public const string InlineScriptRequired = "inline-script-required";

    /// <summary>F-022: the inline script is longer than <c>PipelineScriptLimits.MaxScriptBytes</c>.</summary>
    public const string InlineScriptTooLong = "inline-script-too-long";

    public const string RepositoryUrlRequired = "repository-url-required";
    public const string RepositoryUrlTooLong = "repository-url-too-long";
    public const string RepositoryUrlInvalid = "repository-url-invalid";

    public const string DefaultBranchRequired = "default-branch-required";
    public const string DefaultBranchInvalid = "default-branch-invalid";

    public const string PipelinePathTooLong = "pipeline-path-too-long";
    public const string PipelinePathInvalid = "pipeline-path-invalid";

    /// <summary>F-020: the Freestyle step list was refused (the message carries the field path).</summary>
    public const string StepsInvalid = "steps-invalid";

    /// <summary>F-020: a Freestyle project's steps were never saved, so there is nothing to run.</summary>
    public const string StepsMissing = "steps-missing";

    /// <summary>F-021: the parameter list was refused (the log carries the specific reason).</summary>
    public const string ParametersInvalid = "parameters-invalid";

    /// <summary>F-028: disabling without a reason - the refused trigger has to be able to say why.</summary>
    public const string DisabledReasonRequired = "disabled-reason-required";

    /// <summary>F-028: the reason is longer than <see cref="ProjectValidation.MaxDisabledReasonLength"/>.</summary>
    public const string DisabledReasonTooLong = "disabled-reason-too-long";

    /// <summary>F-028: the reason carries control characters (it is rendered verbatim in a notice).</summary>
    public const string DisabledReasonInvalid = "disabled-reason-invalid";

    /// <summary>F-029: the concurrency limit is not a whole number in the accepted range.</summary>
    public const string MaxConcurrentRunsInvalid = "max-concurrent-runs-invalid";

    /// <summary>F-029: the quiet period is not a whole number of seconds in the accepted range.</summary>
    public const string QuietPeriodInvalid = "quiet-period-invalid";

    /// <summary>F-029: the queue cap is not a whole number in the accepted range.</summary>
    public const string MaxQueuedRunsInvalid = "max-queued-runs-invalid";

    /// <summary>Card F-025: the poll interval is not a whole number inside its bounds.</summary>
    public const string PollIntervalInvalid = "poll-interval-invalid";

    /// <summary>Card F-025 (AC-7): polling was switched on for a project with no repository.</summary>
    public const string PollingNotPipeline = "polling-not-pipeline";

    /// <summary>The form field the code belongs to, or <see langword="null"/> for an unknown code.</summary>
    public static string? FieldFor(string? code) => code switch
    {
        NameRequired or NameTooLong or NameInvalid or NameDuplicate => ProjectFields.Name,
        FolderInvalid or FolderNotFound => ProjectFields.Folder,
        KindInvalid => ProjectFields.Kind,
        PipelineSourceInvalid => ProjectFields.PipelineSource,
        InlineScriptRequired or InlineScriptTooLong => ProjectFields.InlineScript,
        RepositoryUrlRequired or RepositoryUrlTooLong or RepositoryUrlInvalid => ProjectFields.RepositoryUrl,
        DefaultBranchRequired or DefaultBranchInvalid => ProjectFields.DefaultBranch,
        PipelinePathTooLong or PipelinePathInvalid => ProjectFields.PipelinePath,
        StepsInvalid or StepsMissing => ProjectFields.Steps,
        ParametersInvalid => ProjectFields.Parameters,
        DisabledReasonRequired or DisabledReasonTooLong or DisabledReasonInvalid => ProjectFields.DisabledReason,
        MaxConcurrentRunsInvalid => ProjectFields.MaxConcurrentRuns,
        QuietPeriodInvalid => ProjectFields.QuietPeriodSeconds,
        MaxQueuedRunsInvalid => ProjectFields.MaxQueuedRuns,
        PollIntervalInvalid or PollingNotPipeline => ProjectFields.MaxConcurrentRuns,
        _ => null,
    };

    /// <summary>The message rendered next to the field (and in the form-level alert).</summary>
    /// <summary>The message rendered next to the field (and in the form-level alert), in the UI's language.</summary>
    public static string? MessageFor(string? code, Localizer L10n) => code switch
    {
        NameRequired => L10n["Enter a project name."],
        NameTooLong => L10n["The project name must be at most {0} characters.", ProjectValidation.MaxNameLength],
        NameInvalid => L10n["The project name contains characters that are not allowed."],
        NameDuplicate =>
            L10n["A project with this name already exists in this folder (names are compared ignoring case). Rename it, or choose another folder."],
        FolderInvalid => L10n["That is not a folder id."],
        FolderNotFound => L10n["That folder does not exist in this organization."],
        KindInvalid => L10n["The project kind is not one of the known kinds."],
        PipelineSourceInvalid => L10n["The pipeline definition source is not one of the known sources."],
        BranchDiscoveryInvalid => L10n["The branch discovery mode is not one of the known modes."],
        BranchDiscoveryUnavailable =>
            L10n["Only a repository Pipeline can discover branches: a Freestyle or inline Pipeline has no repository to read."],
        InlineScriptRequired => L10n["Enter the pipeline script - an inline Pipeline has no repository file to read."],
        InlineScriptTooLong =>
            L10n["The pipeline script must be at most {0} KiB.", PipelineScriptLimits.MaxScriptBytes / 1024],
        RepositoryUrlRequired => L10n["Enter the Git repository URL."],
        RepositoryUrlTooLong => L10n["The repository URL must be at most {0} characters.", ProjectValidation.MaxRepositoryUrlLength],
        RepositoryUrlInvalid =>
            L10n["Only HTTPS or SSH git URLs are supported, for example https://host/owner/repo.git or git@host:owner/repo.git. file://, ext:: and other transports are rejected."],
        DefaultBranchRequired => L10n["Enter the default branch."],
        DefaultBranchInvalid => L10n["That is not a valid branch name."],
        PipelinePathTooLong => L10n["The pipeline path must be at most {0} characters.", ProjectValidation.MaxPipelinePathLength],
        PipelinePathInvalid =>
            L10n["Enter a relative path inside the repository (for example pipeline.lua). Absolute paths, backslashes and '..' are rejected."],
        StepsInvalid =>
            L10n["The step list was refused - check every step's name and script, then try again."],
        StepsMissing =>
            L10n["This Freestyle project has no steps yet: save the steps before triggering a run."],
        ParametersInvalid =>
            L10n["The parameter list was refused - check every parameter's name, type and choices."],
        DisabledReasonRequired =>
            L10n["Say why the project is being disabled - the person whose trigger is refused reads it."],
        DisabledReasonTooLong =>
            L10n["The reason must be at most {0} characters.", ProjectValidation.MaxDisabledReasonLength],
        DisabledReasonInvalid => L10n["The reason cannot contain control characters."],
        MaxConcurrentRunsInvalid =>
            L10n["The concurrency limit is a whole number between {0} and {1}; leave it blank for unlimited.", ProjectValidation.MinMaxConcurrentRuns, ProjectValidation.MaxMaxConcurrentRuns],
        QuietPeriodInvalid =>
            L10n["The quiet period is a whole number of seconds between 0 and {0}.", ProjectValidation.MaxQuietPeriodSeconds],
        MaxQueuedRunsInvalid =>
            L10n["The queue cap is a whole number between {0} and {1}; leave it blank for unlimited.", ProjectValidation.MinMaxQueuedRuns, ProjectValidation.MaxMaxQueuedRuns],
        PollIntervalInvalid =>
            L10n["The poll interval is a whole number of minutes between {0} and {1}.", ProjectValidation.MinPollIntervalMinutes, ProjectValidation.MaxPollIntervalMinutes],
        PollingNotPipeline =>
            L10n["Only a project with a repository can be polled: this one is Freestyle or inline, so it has no remote to watch."],
        _ => null,
    };
}

/// <summary>
/// Server-side validation for PRD §3.2 project fields (card F-003). Every rule here is re-checked by
/// <see cref="ProjectService"/>; the HTML form's <c>required</c>/<c>maxlength</c> attributes are a
/// convenience, never the enforcement.
/// <para>
/// <b>Repository URL allow-list.</b> Only <c>https://</c> and <c>ssh://</c> URLs and git's scp-like
/// <c>user@host:path</c> form are accepted. That matters because git can be told to run a transport
/// helper instead of a network protocol: <c>file://</c> reads the server's disk and <c>ext::</c>
/// runs an arbitrary command. An allow-list keeps future git transports out by default instead of
/// chasing a deny-list.
/// </para>
/// </summary>
public static class ProjectValidation
{
    /// <summary>PRD §3.2's default pipeline file name.</summary>
    public const string DefaultPipelinePath = "pipeline.lua";

    /// <summary>F-028: the longest disable reason (it is rendered verbatim in a notice).</summary>
    public const int MaxDisabledReasonLength = 500;

    /// <summary>F-029: the accepted concurrency range - a cap of 0 is a configured starvation, and a
    /// cap above the worker fleet's own ceiling would be a setting that never binds.</summary>
    public const int MinMaxConcurrentRuns = 1;
    public const int MaxMaxConcurrentRuns = 100;

    /// <summary>F-029: the accepted quiet period range, in seconds (zero .. one day).</summary>
    public const int MinQuietPeriodSeconds = 0;
    public const int MaxQuietPeriodSeconds = 86_400;

    /// <summary>F-029: the accepted queue-cap range.</summary>
    public const int MinMaxQueuedRuns = 1;
    public const int MaxMaxQueuedRuns = 1_000;

    /// <summary>Card F-025: the shortest poll interval (the card's own minimum).</summary>
    public const int MinPollIntervalMinutes = 1;

    /// <summary>Card F-025: the longest poll interval - a day. Anything longer is not polling any more;
    /// a schedule (F-024) is the tool for "build once a week".</summary>
    public const int MaxPollIntervalMinutes = 1_440;

    public const int MaxNameLength = 200;
    public const int MaxRepositoryUrlLength = 2048;
    public const int MaxDefaultBranchLength = 200;
    public const int MaxPipelinePathLength = 512;

    // git's scp-like syntax: user@host:path. The path must be relative (git treats a leading '/'
    // as an absolute server path) and may not contain a colon, because a second colon makes the
    // value ambiguous enough that a transport helper could be selected.
    private static readonly Regex ScpLikeRepositoryUrl = new(
        "^(?<user>[A-Za-z0-9][A-Za-z0-9._-]*)@(?<host>[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?)"
        + ":(?<path>(?!/)(?!-)[^\\s:]+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Validates and trims the disable reason (card F-028). Required - a refused trigger has to be able
    /// to say why - and bounded, because the reason travels verbatim into the trigger form's refusal,
    /// the webhook delivery's outcome and the log.
    /// </summary>
    public static string? ValidateDisabledReason(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;

        if (normalized.Length == 0)
        {
            return ProjectErrorCodes.DisabledReasonRequired;
        }

        if (normalized.Length > MaxDisabledReasonLength)
        {
            return ProjectErrorCodes.DisabledReasonTooLong;
        }

        return normalized.Any(char.IsControl) ? ProjectErrorCodes.DisabledReasonInvalid : null;
    }

    /// <summary>
    /// Validates the three queue controls (card F-029). Blank means "the default": unlimited for the
    /// two caps, zero for the quiet period - so a form that omits a field keeps today's behaviour. The
    /// three are checked independently and the first error code is returned alongside the parsed values;
    /// nothing is written unless every one of them is legal.
    /// </summary>
    public static string? ValidateLimits(
        ProjectLimitsInput input,
        out ProjectLimits limits)
    {
        ArgumentNullException.ThrowIfNull(input);

        limits = new ProjectLimits(null, MinQuietPeriodSeconds, null);
        string? error = null;

        var maxConcurrent = (int?)null;
        if (!string.IsNullOrWhiteSpace(input.MaxConcurrentRuns))
        {
            if (!TryParseBounded(
                    input.MaxConcurrentRuns, MinMaxConcurrentRuns, MaxMaxConcurrentRuns, out var parsed))
            {
                error = ProjectErrorCodes.MaxConcurrentRunsInvalid;
            }

            maxConcurrent = parsed;
        }

        var quietPeriod = MinQuietPeriodSeconds;
        if (!string.IsNullOrWhiteSpace(input.QuietPeriodSeconds))
        {
            if (!TryParseBounded(
                    input.QuietPeriodSeconds, MinQuietPeriodSeconds, MaxQuietPeriodSeconds, out var parsed))
            {
                error ??= ProjectErrorCodes.QuietPeriodInvalid;
            }

            quietPeriod = parsed;
        }

        var maxQueued = (int?)null;
        if (!string.IsNullOrWhiteSpace(input.MaxQueuedRuns))
        {
            if (!TryParseBounded(input.MaxQueuedRuns, MinMaxQueuedRuns, MaxMaxQueuedRuns, out var parsed))
            {
                error ??= ProjectErrorCodes.MaxQueuedRunsInvalid;
            }

            maxQueued = parsed;
        }

        limits = new ProjectLimits(maxConcurrent, quietPeriod, maxQueued);
        return error;
    }

    /// <summary>Whole number in <paramref name="min"/>..<paramref name="max"/> (invariant culture, no
    /// signs, no decimals); anything else is refused instead of silently coerced.</summary>
    /// <summary>
    /// Validates the polling card (card F-025): the switch, and - when it is on - a whole-number interval in
    /// minutes, defaulting to <c>PollOptions.DefaultIntervalMinutes</c> when the field is left blank. Turning
    /// polling off stores no interval at all.
    /// </summary>
    public static string? ValidatePolling(ProjectPollingInput input, out ProjectPolling polling)
    {
        ArgumentNullException.ThrowIfNull(input);

        var enabled = input.PollingEnabled is { } flag
            && (flag.Equals("true", StringComparison.OrdinalIgnoreCase)
                || flag.Equals("on", StringComparison.OrdinalIgnoreCase)
                || flag == "1");

        polling = new ProjectPolling(false, null);
        if (!enabled)
        {
            return null;
        }

        var interval = Zenkins.Server.Polling.PollOptions.DefaultIntervalMinutes;
        if (!string.IsNullOrWhiteSpace(input.PollIntervalMinutes))
        {
            if (!TryParseBounded(
                    input.PollIntervalMinutes,
                    MinPollIntervalMinutes,
                    MaxPollIntervalMinutes,
                    out var parsed))
            {
                polling = new ProjectPolling(true, null);
                return ProjectErrorCodes.PollIntervalInvalid;
            }

            interval = parsed;
        }

        polling = new ProjectPolling(true, interval);
        return null;
    }

    private static bool TryParseBounded(string text, int min, int max, out int value)
    {
        value = min;
        if (!int.TryParse(
                text.Trim(),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
            || parsed < min
            || parsed > max)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// Validates and trims the project name. Returns a <see cref="ProjectErrorCodes"/> value or null.
    /// </summary>
    public static string? ValidateName(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;

        if (normalized.Length == 0)
        {
            return ProjectErrorCodes.NameRequired;
        }

        if (normalized.Length > MaxNameLength)
        {
            return ProjectErrorCodes.NameTooLong;
        }

        return normalized.Any(char.IsControl) ? ProjectErrorCodes.NameInvalid : null;
    }

    /// <summary>
    /// Validates the kind selector (card F-020). Blank means <see cref="ProjectKinds.Pipeline"/> -
    /// the historical default, so an existing client that posts no kind keeps the F-003 behaviour -
    /// and anything that is not a known kind is refused rather than coerced.
    /// </summary>
    public static string? ValidateKind(string? value, out string kind)
    {
        kind = value?.Trim() ?? string.Empty;
        if (kind.Length == 0)
        {
            kind = ProjectKinds.Pipeline;
            return null;
        }

        return ProjectKinds.IsValid(kind) ? null : ProjectErrorCodes.KindInvalid;
    }

    /// <summary>
    /// Validates the definition-source selector (card F-022). Blank means
    /// <see cref="ProjectSources.Repository"/> - the historical default, so a client that posts no
    /// source keeps the F-003/F-020 behaviour - and anything unknown is refused rather than coerced.
    /// The source is only read on create: like the kind, an edit never rewrites it.
    /// </summary>
    public static string? ValidatePipelineSource(string? value, out string source)
    {
        source = value?.Trim() ?? string.Empty;
        if (source.Length == 0)
        {
            source = ProjectSources.Repository;
            return null;
        }

        return ProjectSources.IsValid(source) ? null : ProjectErrorCodes.PipelineSourceInvalid;
    }

    /// <summary>
    /// Validates the branch-discovery selector (card F-023). Blank means
    /// <see cref="BranchDiscoveryModes.Single"/> - the historical default, so a client that posts no
    /// discovery mode keeps today's one-branch behaviour - and anything unknown is refused rather than
    /// coerced.
    /// </summary>
    public static string? ValidateBranchDiscovery(string? value, out string mode)
    {
        mode = value?.Trim() ?? string.Empty;
        if (mode.Length == 0)
        {
            mode = BranchDiscoveryModes.Single;
            return null;
        }

        return BranchDiscoveryModes.IsKnown(mode) ? null : ProjectErrorCodes.BranchDiscoveryInvalid;
    }

    /// <summary>
    /// Whether the project shape can honour <paramref name="mode"/>. Only a repository Pipeline has
    /// branches: a Freestyle project's steps and an inline Pipeline's script live on the Server, and
    /// discovery for either would store a setting no code path could act on.
    /// </summary>
    public static bool CanDiscoverBranches(string kind, string pipelineSource) =>
        kind == ProjectKinds.Pipeline && pipelineSource == ProjectSources.Repository;

    /// <summary>
    /// Validates an inline Pipeline's Lua body (card F-022). Required and bounded by bytes (the same
    /// UTF-8 bound the Worker re-checks): an inline project has no repository file to fall back on, and
    /// an empty script would freeze a run that cannot define anything. The text is stored verbatim -
    /// trimming would move every diagnostic's line/column.
    /// </summary>
    public static string? ValidateInlineScript(string? value, out string script)
    {
        script = value ?? string.Empty;
        if (script.Trim().Length == 0)
        {
            return ProjectErrorCodes.InlineScriptRequired;
        }

        return System.Text.Encoding.UTF8.GetByteCount(script) > PipelineScriptLimits.MaxScriptBytes
            ? ProjectErrorCodes.InlineScriptTooLong
            : null;
    }


    /// <summary>
    /// Validates and trims the repository URL. Only HTTPS, SSH and the scp-like SSH form pass; every
    /// other scheme (including <c>file</c> and <c>ext</c>) is rejected with one stable message.
    /// </summary>
    public static string? ValidateRepositoryUrl(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;

        if (normalized.Length == 0)
        {
            return ProjectErrorCodes.RepositoryUrlRequired;
        }

        if (normalized.Length > MaxRepositoryUrlLength)
        {
            return ProjectErrorCodes.RepositoryUrlTooLong;
        }

        // No whitespace or control characters anywhere. This alone rejects git's transport helper
        // form ("ext::sh -c ..."), which needs a command line to be useful, and it keeps the value
        // safe to log and to hand to a process argument later.
        if (normalized.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            return ProjectErrorCodes.RepositoryUrlInvalid;
        }

        // A leading dash would be parsed as an option by git if the value were ever passed through
        // unguarded; reject it instead of relying on a later "--" separator.
        if (normalized[0] == '-')
        {
            return ProjectErrorCodes.RepositoryUrlInvalid;
        }

        if (normalized.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            {
                return ProjectErrorCodes.RepositoryUrlInvalid;
            }

            // The allow-list: only these two schemes can reach a repository. file:// is a read of
            // the server filesystem and git:// is unauthenticated and unencrypted, so neither is
            // accepted even though git supports them.
            var schemeIsSupported =
                string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase);
            if (!schemeIsSupported)
            {
                return ProjectErrorCodes.RepositoryUrlInvalid;
            }

            if (string.IsNullOrWhiteSpace(uri.Host)
                || string.IsNullOrEmpty(uri.AbsolutePath)
                || uri.AbsolutePath == "/"
                || uri.Query.Length > 0
                || uri.Fragment.Length > 0)
            {
                return ProjectErrorCodes.RepositoryUrlInvalid;
            }

            return null;
        }

        // No scheme: the only remaining supported shape is git's scp-like SSH syntax.
        return ScpLikeRepositoryUrl.IsMatch(normalized)
            ? null
            : ProjectErrorCodes.RepositoryUrlInvalid;
    }

    /// <summary>
    /// Validates and trims the default branch. These are git ref-shape checks, not a full
    /// <c>git check-ref-format</c> implementation: they reject the values that turn into paths
    /// (<c>..</c>, leading slash) or command-line options (leading dash) and the characters git
    /// itself forbids in a ref.
    /// </summary>
    public static string? ValidateDefaultBranch(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;

        if (normalized.Length == 0)
        {
            return ProjectErrorCodes.DefaultBranchRequired;
        }

        if (normalized.Length > MaxDefaultBranchLength)
        {
            return ProjectErrorCodes.DefaultBranchInvalid;
        }

        if (normalized[0] is '/' or '-'
            || normalized.Contains("..", StringComparison.Ordinal)
            || normalized.Contains('\\')
            || normalized.Contains("//", StringComparison.Ordinal)
            || normalized.Contains("@{", StringComparison.Ordinal)
            || normalized.IndexOfAny(['~', '^', ':', '?', '*', '[', '\u007f']) >= 0
            || normalized.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            || normalized.EndsWith('/')
            || normalized.EndsWith('.')
            || normalized.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectErrorCodes.DefaultBranchInvalid;
        }

        return null;
    }

    /// <summary>
    /// Validates the pipeline path and applies the PRD default when it is blank. A repository-relative
    /// path only: no leading slash, no drive/UNC prefix, no backslashes and no <c>..</c> segment.
    /// </summary>
    public static string? ValidatePipelinePath(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            normalized = DefaultPipelinePath;
        }

        if (normalized.Length > MaxPipelinePathLength)
        {
            return ProjectErrorCodes.PipelinePathTooLong;
        }

        if (normalized[0] is '/' or '\\'
            || normalized.Contains("..", StringComparison.Ordinal)
            || normalized.Contains('\\')
            || normalized.Contains("//", StringComparison.Ordinal)
            || normalized.EndsWith('/')
            || normalized.Any(char.IsControl)
            || (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':'))
        {
            return ProjectErrorCodes.PipelinePathInvalid;
        }

        return null;
    }
}
