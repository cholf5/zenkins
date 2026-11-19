using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;
using Zenkins.Server.Organizations;

namespace Zenkins.Server.Auth;

/// <summary>Result of a registration attempt. <see cref="Errors"/> are safe to display.</summary>
public sealed record RegistrationResult(
    bool Succeeded,
    BootstrapOutcome Bootstrap,
    IReadOnlyList<string> Errors)
{
    /// <summary>Failed attempt; <paramref name="errors"/> come from Identity's validators.</summary>
    public static RegistrationResult Failure(IEnumerable<string> errors) =>
        new(false, BootstrapOutcome.NotAttempted, errors.ToArray());
}

/// <summary>
/// Registration (card F-001). The first successful registration creates the account, the starter
/// organization and the Owner membership in <b>one</b> transaction; every later registration creates
/// an account without an organization (invitations are F-002's job).
/// <para>
/// The <see cref="UserManager{TUser}"/> and the injected <see cref="ZenkinsDbContext"/> are the same
/// scoped instance (the EF stores are registered against that context), which is what lets
/// <see cref="UserManager{TUser}.CreateAsync"/> and the bootstrap share one transaction. Do not
/// replace the context registration with a factory for this service without re-verifying that.
/// </para>
/// </summary>
public sealed class RegistrationService(
    UserManager<User> userManager,
    ZenkinsDbContext context,
    IBootstrapService bootstrap,
    ILogger<RegistrationService> logger)
{
    /// <summary>
    /// Registers <paramref name="email"/> with <paramref name="password"/>. Identity hashes the
    /// password; this service never sees or stores the hash itself and never logs either.
    /// </summary>
    public Task<RegistrationResult> RegisterAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default) =>
        RegisterCoreAsync(email, password, organizationName: null, cancellationToken);

    /// <summary>
    /// The setup wizard's registration (F-044): the account, the organization named by the operator,
    /// the Owner membership and the marker commit together. Unlike <see cref="RegisterAsync"/>, this
    /// path never half-succeeds: when the marker already exists (another browser finished setup
    /// first) the call writes nothing and returns a readable refusal, because an operator who was
    /// told they were setting up the instance must not silently become an account without an
    /// organization.
    /// </summary>
    public Task<RegistrationResult> RegisterOwnerAsync(
        string email,
        string password,
        string organizationName,
        CancellationToken cancellationToken = default)
    {
        var trimmedName = organizationName?.Trim();
        if (string.IsNullOrEmpty(trimmedName))
        {
            return Task.FromResult(RegistrationResult.Failure([EmptyOrganizationNameMessage]));
        }

        if (trimmedName.Length > OrganizationManagementService.MaxOrganizationNameLength)
        {
            return Task.FromResult(RegistrationResult.Failure([LongOrganizationNameMessage]));
        }

        return RegisterCoreAsync(email, password, trimmedName, cancellationToken);
    }

    private async Task<RegistrationResult> RegisterCoreAsync(
        string email,
        string password,
        string? organizationName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentNullException.ThrowIfNull(password);

        // The wizard path refuses before the transaction: a completed setup must not create an
        // account at all, not even one that is then rolled back.
        if (organizationName is not null && await bootstrap.HasCompletedAsync(cancellationToken))
        {
            return RegistrationResult.Failure([SetupAlreadyCompletedMessage]);
        }

        email = email.Trim();
        var user = new User { UserName = email, Email = email };

        await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            IdentityResult created;
            try
            {
                created = await userManager.CreateAsync(user, password);
            }
            catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception))
            {
                // Two simultaneous registrations for the same address: Identity's validator cannot
                // see the other transaction yet, so the unique index is what catches it.
                await transaction.RollbackAsync(cancellationToken);
                return RegistrationResult.Failure([DuplicateAccountMessage]);
            }

            if (!created.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);
                return RegistrationResult.Failure(created.Errors.Select(error => error.Description));
            }

            if (organizationName is null && await bootstrap.HasCompletedAsync(cancellationToken))
            {
                // Not a first run: the account is created without an organization and the UI shows
                // the explicit "no organization yet" state until an Owner invites them (F-002).
                await transaction.CommitAsync(cancellationToken);
                logger.LogInformation(
                    "User registered without an organization; first-run bootstrap already completed. UserId={UserId}",
                    user.Id);
                return new RegistrationResult(true, BootstrapOutcome.NotAttempted, []);
            }

            var outcome = await bootstrap.TryCreateOwnerOrganizationAsync(user, organizationName, cancellationToken);
            if (outcome == BootstrapOutcome.Created)
            {
                await transaction.CommitAsync(cancellationToken);
                logger.LogInformation(
                    "First user registered and bootstrapped as organization Owner. UserId={UserId}",
                    user.Id);
                return new RegistrationResult(true, BootstrapOutcome.Created, []);
            }

            if (organizationName is not null)
            {
                // The wizard lost the race: this browser's account and name belong to a transaction
                // that is about to roll back, and the other browser owns the organization.
                await transaction.RollbackAsync(cancellationToken);
                context.ChangeTracker.Clear();
                logger.LogInformation(
                    "Setup registration lost the first-run race; no account or organization was written for this attempt. UserId={UserId}",
                    user.Id);
                return RegistrationResult.Failure([SetupAlreadyCompletedMessage]);
            }
        }

        // Only reachable when the marker insert lost a race: everything above (including the new
        // account) was rolled back, and exactly one other transaction owns the starter organization.
        context.ChangeTracker.Clear();

        var fallbackUser = new User { UserName = email, Email = email };
        IdentityResult fallbackCreated;
        try
        {
            fallbackCreated = await userManager.CreateAsync(fallbackUser, password);
        }
        catch (DbUpdateException exception) when (DatabaseErrors.IsUniqueViolation(exception))
        {
            return RegistrationResult.Failure([DuplicateAccountMessage]);
        }

        if (!fallbackCreated.Succeeded)
        {
            return RegistrationResult.Failure(fallbackCreated.Errors.Select(error => error.Description));
        }

        logger.LogInformation(
            "User registered without an organization; another registration won the first-run bootstrap. UserId={UserId}",
            fallbackUser.Id);
        return new RegistrationResult(true, BootstrapOutcome.AlreadyCompleted, []);
    }

    private const string DuplicateAccountMessage =
        "An account with this email address already exists.";

    /// <summary>Shown by the wizard when the setup page is submitted with no organization name.</summary>
    private const string EmptyOrganizationNameMessage = "Enter an organization name.";

    /// <summary>Shown by the wizard when the organization name exceeds the column's bound.</summary>
    private const string LongOrganizationNameMessage =
        "The organization name must be at most 200 characters.";

    /// <summary>
    /// Shown when a wizard submission finds the marker already present - the other browser's setup
    /// committed first, or this browser reloaded a finished setup. Public because the wizard page
    /// localizes this one message at its display boundary (see <c>SetupOwner</c>).
    /// </summary>
    public const string SetupAlreadyCompletedMessage =
        "Setup has already been completed. Sign in instead.";
}
