using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Zenkins.Server.Data;
using Zenkins.Server.Data.Entities;

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
    BootstrapService bootstrap,
    ILogger<RegistrationService> logger)
{
    /// <summary>
    /// Registers <paramref name="email"/> with <paramref name="password"/>. Identity hashes the
    /// password; this service never sees or stores the hash itself and never logs either.
    /// </summary>
    public async Task<RegistrationResult> RegisterAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentNullException.ThrowIfNull(password);

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

            if (await bootstrap.HasCompletedAsync(cancellationToken))
            {
                // Not a first run: the account is created without an organization and the UI shows
                // the explicit "no organization yet" state until an Owner invites them (F-002).
                await transaction.CommitAsync(cancellationToken);
                logger.LogInformation(
                    "User registered without an organization; first-run bootstrap already completed. UserId={UserId}",
                    user.Id);
                return new RegistrationResult(true, BootstrapOutcome.NotAttempted, []);
            }

            var outcome = await bootstrap.TryCreateOwnerOrganizationAsync(user, cancellationToken);
            if (outcome == BootstrapOutcome.Created)
            {
                await transaction.CommitAsync(cancellationToken);
                logger.LogInformation(
                    "First user registered and bootstrapped as organization Owner. UserId={UserId}",
                    user.Id);
                return new RegistrationResult(true, BootstrapOutcome.Created, []);
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
}
