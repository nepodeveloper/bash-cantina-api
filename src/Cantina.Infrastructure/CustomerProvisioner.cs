using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cantina.Infrastructure;

/// <summary>
/// Creates customer accounts as one atomic unit. Identity performs several writes per account,
/// so the transaction is what guarantees there is never a user without a role, or an account
/// without its external login.
/// </summary>
public sealed class CustomerProvisioner(UserManager<ApplicationUser> users, CantinaDbContext db)
{
    public async Task<IdentityResult> CreateCustomerAsync(ApplicationUser user, string? password, UserLoginInfo? login)
    {
        var outcome = IdentityResult.Success;
        // The retrying execution strategy owns the transaction so a transient failure replays the whole unit.
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync();
            outcome = password is null ? await users.CreateAsync(user) : await users.CreateAsync(user, password);
            if (outcome.Succeeded) outcome = await users.AddToRoleAsync(user, "Customer");
            if (outcome.Succeeded && login is not null) outcome = await users.AddLoginAsync(user, login);
            if (outcome.Succeeded) await transaction.CommitAsync();
            else await transaction.RollbackAsync();
        });
        return outcome;
    }
}
