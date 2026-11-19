using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;

namespace Zenkins.Tests.Auth;

/// <summary>
/// The F-044 first-run marker, for fixtures that seed their accounts instead of registering them.
/// <para>
/// <c>SetupGateMiddleware</c> answers <c>/account/login</c> and <c>/account/register</c> with a 302 to
/// <c>/setup</c> while <see cref="BootstrapMarker"/> is absent — that is the whole point of the gate,
/// and it is invisible to a fixture that only inserts rows. The symptom is not a setup failure: a
/// page test on such a fixture dies on the sign-in step with <c>302 (Found)</c> from
/// <c>AuthTestHost.LoginAsync</c>, several frames away from the cause (that is exactly how the
/// dashboard suite spent a while red). A fixture that seeds "the state a completed bootstrap leaves
/// behind" — an Owner with an organization — has to seed this row too.
/// </para>
/// <para>
/// Fixtures that drive the real registration or the wizard (<see cref="AuthTestHost.RegisterAsync"/>,
/// <see cref="Zenkins.Tests.Organizations.OrganizationTestHost"/>) get the row written by the product
/// and must not add a second one: the primary key is fixed, so a duplicate is a unique-constraint
/// failure rather than a no-op.
/// </para>
/// </summary>
internal static class BootstrapSeed
{
    /// <summary>
    /// Stages the marker on <paramref name="context"/>. Nothing is written here: the row joins
    /// whatever batch the caller commits, so a synchronous seed can add it next to its other rows and
    /// an asynchronous one can follow with <c>SaveChangesAsync</c>. The caller is already in a
    /// system scope — the row exists before any tenant can be selected.
    /// </summary>
    /// <param name="context">The fixture's context, opened inside an <c>ISystemDataScope</c>.</param>
    /// <param name="organizationId">The organization the bootstrap created; any of the seeded ones is
    /// the truth as far as the gate is concerned, since it only asks whether a row exists.</param>
    /// <param name="userId">The first Owner's id. Deliberately not a foreign key, so the order the
    /// fixture adds rows in does not matter.</param>
    public static void AddBootstrapMarker(this ZenkinsDbContext context, Guid organizationId, string userId) =>
        context.BootstrapMarkers.Add(new BootstrapMarker
        {
            OrganizationId = organizationId,
            UserId = userId,
        });
}
