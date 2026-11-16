using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Zenkins.Server.Schedules;

/// <summary>
/// Card F-024's form endpoints: the settings tab's Schedules card creates, toggles and deletes schedules, and
/// previews the next three due times before a save. They follow the project forms' shape exactly - form-bound (so
/// <c>UseAntiforgery</c> validates the token), cookie-authenticated, PRG outcomes with a stable
/// <c>?error=</c>/<c>?notice=</c> code, and the service (not the endpoint) decides membership.
/// <para>
/// <b>The route is authoritative.</b> The form models carry no project or schedule id, so a forged field cannot
/// redirect a mutation at another project; the service resolves every id through the tenant filter, which is what
/// makes a foreign id the same answer as a missing one.
/// </para>
/// <para>
/// <b>The preview is a POST, not a GET.</b> It reads the values the form is holding, so it belongs with the other
/// form posts; the answer is a redirect that echoes the expression and the zone, and the page recomputes the three
/// due times from them on the render that follows. Nothing is stored by a preview.
/// </para>
/// </summary>
public static class ScheduleEndpoints
{
    /// <summary>The page the card lives on (every PRG target below).</summary>
    public static string SettingsPath(Guid projectId) => $"/projects/{projectId}/settings";

    public const string NoticeCreated = "schedule-created";
    public const string NoticeDeleted = "schedule-deleted";
    public const string NoticeEnabled = "schedule-enabled";
    public const string NoticeDisabled = "schedule-disabled";
    public const string NoticeQueueAnyway = "schedule-queue-anyway";
    public const string NoticeSkipOverlap = "schedule-skip-overlap";

    /// <summary>The two switches the toggle endpoint owns (a closed set; anything else is a field error).</summary>
    public const string FlagEnabled = "enabled";
    public const string FlagQueueAnyway = "queue-anyway";

    /// <summary>Maps the three schedule mutations. All require authentication.</summary>
    public static IEndpointRouteBuilder MapScheduleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var schedules = endpoints.MapGroup("/projects/{projectId:guid}/schedules").RequireAuthorization();
        schedules.MapPost("/create", CreateAsync);
        schedules.MapPost("/{scheduleId:guid}/toggle", ToggleAsync);
        schedules.MapPost("/{scheduleId:guid}/delete", DeleteAsync);
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        Guid projectId,
        [FromForm] ScheduleForm form,
        HttpContext httpContext,
        ScheduleService schedules,
        TimeProvider clock)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        // A lost body is the page's PRG error, never a null reference (F-019's rule).
        var input = form?.ToInput() ?? new ScheduleInput(null, null, QueueAnyway: false, Enabled: true);

        // "Preview" is the same form's second submit: it validates and echoes, and stores nothing.
        if (string.Equals(form?.Action, "preview", StringComparison.OrdinalIgnoreCase))
        {
            var preview = SchedulePreview.Compute(input.Cron, input.TimeZone, clock.GetUtcNow());
            var fields = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["previewCron"] = input.Cron,
                ["previewZone"] = input.TimeZone,
            };
            if (!preview.IsValid)
            {
                fields["error"] = preview.ErrorCode;
            }

            return Results.Redirect($"{SettingsPath(projectId)}{QueryString.Create(fields)}");
        }

        var result = await schedules.CreateAsync(userId, projectId, input, httpContext.RequestAborted);
        return result.Status switch
        {
            ScheduleManagementStatus.Success =>
                Results.Redirect($"{SettingsPath(projectId)}?notice={NoticeCreated}"),
            ScheduleManagementStatus.Forbidden => Forbidden(result),
            ScheduleManagementStatus.NotFound => Results.NotFound(),
            _ => Results.Redirect($"{SettingsPath(projectId)}?error={result.ErrorCode}"),
        };
    }

    private static async Task<IResult> ToggleAsync(
        Guid projectId,
        Guid scheduleId,
        [FromForm] ScheduleToggleForm form,
        HttpContext httpContext,
        ScheduleService schedules)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var flag = form?.Flag;
        var value = IsChecked(form?.Value);

        var result = await schedules.SetFlagAsync(
            userId,
            projectId,
            scheduleId,
            flag,
            value,
            httpContext.RequestAborted);
        return result.Status switch
        {
            ScheduleManagementStatus.Success => Results.Redirect(
                $"{SettingsPath(projectId)}?notice={NoticeForToggle(flag, value)}"),
            ScheduleManagementStatus.Forbidden => Forbidden(result),
            ScheduleManagementStatus.NotFound => Results.NotFound(),
            _ => Results.Redirect($"{SettingsPath(projectId)}?error={result.ErrorCode}"),
        };
    }

    private static async Task<IResult> DeleteAsync(
        Guid projectId,
        Guid scheduleId,
        [FromForm] ScheduleDeleteForm form,
        HttpContext httpContext,
        ScheduleService schedules)
    {
        var userId = FindUserId(httpContext);
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var result = await schedules.DeleteAsync(
            userId,
            projectId,
            scheduleId,
            httpContext.RequestAborted);
        return result.Status switch
        {
            ScheduleManagementStatus.Success =>
                Results.Redirect($"{SettingsPath(projectId)}?notice={NoticeDeleted}"),
            ScheduleManagementStatus.Forbidden => Forbidden(result),
            ScheduleManagementStatus.NotFound => Results.NotFound(),
            _ => Results.Redirect($"{SettingsPath(projectId)}?error={result.ErrorCode}"),
        };
    }

    /// <summary>The notice a toggle PRG answers with, so the page can confirm the state it now shows.</summary>
    private static string NoticeForToggle(string? flag, bool value)
    {
        if (string.Equals(flag?.Trim(), FlagQueueAnyway, StringComparison.OrdinalIgnoreCase))
        {
            return value ? NoticeQueueAnyway : NoticeSkipOverlap;
        }

        return value ? NoticeEnabled : NoticeDisabled;
    }

    /// <summary>
    /// Whether an HTML checkbox was checked. The card renders each box <em>before</em> a hidden <c>false</c>
    /// companion, so an unchecked box posts <c>false</c> and a checked one posts <c>true,false</c> (the model
    /// binder may hand over either the first value or the joined list) - both spellings are read here, and no
    /// other input is trusted to mean "on" except the values a browser actually sends.
    /// </summary>
    private static bool IsChecked(string? value)
    {
        if (value is null)
        {
            return false;
        }

        foreach (var token in value.Split(','))
        {
            var trimmed = token.Trim();
            if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("on", StringComparison.OrdinalIgnoreCase)
                || trimmed == "1")
            {
                return true;
            }
        }

        return false;
    }

    private static IResult Forbidden(ScheduleMutationResult result) =>
        Results.Text(
            result.Message ?? "You are not allowed to perform this action.",
            "text/plain",
            statusCode: StatusCodes.Status403Forbidden);

    private static string? FindUserId(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>The Schedules card's create form. Optional text, so a typo reaches the service's validation.</summary>
    public sealed class ScheduleForm
    {
        public string? Cron { get; set; }

        public string? TimeZone { get; set; }

        /// <summary><c>true</c> when checked (the browser posts nothing when unchecked).</summary>
        public string? QueueAnyway { get; set; }

        public string? Enabled { get; set; }

        /// <summary><c>preview</c> for the preview button, empty for the save.</summary>
        public string? Action { get; set; }

        public ScheduleInput ToInput() => new(
            Cron,
            TimeZone,
            IsChecked(QueueAnyway),
            // Absent means unchecked: the card renders the box checked by default, so a save that left it alone
            // still posts `true`. A body that carries neither box (a script posting only the expression) creates a
            // paused schedule rather than an active one - the safer of the two readings, and visible on the page.
            IsChecked(Enabled));
    }

    /// <summary>One switch of one schedule: <c>enabled</c> or <c>queue-anyway</c>.</summary>
    public sealed class ScheduleToggleForm
    {
        public string? Flag { get; set; }

        public string? Value { get; set; }
    }

    /// <summary>Empty on purpose: the parameter makes the delete POST form-bound (antiforgery).</summary>
    public sealed class ScheduleDeleteForm
    {
    }
}
