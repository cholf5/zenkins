using Microsoft.AspNetCore.Mvc;
using Zenkins.Server.Auth;

namespace Zenkins.Server.Localization;

/// <summary>
/// The language switcher's form endpoint. A real POST, not a circuit event: changing the language
/// replaces every rendered string, so the honest answer is a document reload (a new request, a new
/// circuit) rather than a partial re-render that leaves the old language in the parts Blazor did not
/// touch.
/// <para>
/// It is deliberately anonymous: the account pages show the switcher too, and choosing a language is
/// not a state change anyone else can observe. The cookie is HttpOnly - only the server reads it -
/// and the redirect target is sanitized exactly like the sign-in return URL.
/// </para>
/// </summary>
public static class LanguageEndpoints
{
    /// <summary>Maps <c>POST /language</c>.</summary>
    public static IEndpointRouteBuilder MapLanguageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/language", SetLanguageAsync).AllowAnonymous();
        return endpoints;
    }

    private static IResult SetLanguageAsync(
        [FromForm] SetLanguageForm? form,
        HttpContext httpContext)
    {
        // F-019 shape: a body whose fields all fail to bind arrives as null. An unknown or missing
        // language is a client error and sets no cookie - never a silent fallback that looks applied.
        if (form is null || !AppLanguage.IsSupported(form.Language))
        {
            return Results.Text(
                $"The language must be one of: {string.Join(", ", AppLanguage.Choices.Select(choice => choice.Code))}.",
                "text/plain",
                statusCode: StatusCodes.Status400BadRequest);
        }

        httpContext.Response.Cookies.Append(
            AppLanguage.CookieName,
            form.Language!,
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                Path = "/",
                SameSite = SameSiteMode.Lax,
                Secure = httpContext.Request.IsHttps,
                MaxAge = TimeSpan.FromDays(365),
            });

        return Results.Redirect(ReturnUrlSanitizer.Sanitize(form.ReturnUrl, "/"));
    }
}

/// <summary>The switcher's POST body. Both properties are nullable: a malformed body binds to nulls.</summary>
public sealed class SetLanguageForm
{
    /// <summary>The chosen canonical language code.</summary>
    public string? Language { get; set; }

    /// <summary>Where to return after the reload; a site-relative path only.</summary>
    public string? ReturnUrl { get; set; }
}
