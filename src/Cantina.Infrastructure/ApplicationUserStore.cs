using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Cantina.Infrastructure;

public sealed class ApplicationUserStore(CantinaDbContext context, IdentityErrorDescriber? describer = null)
    : UserStore<ApplicationUser, IdentityRole, CantinaDbContext>(context, describer)
{
    public override async Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.CreateAsync(user, cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            TableName: "AspNetUsers",
            ConstraintName: "UserNameIndex" or "EmailIndex"
        } || exception.InnerException is SqliteException
        {
            SqliteErrorCode: 19,
            SqliteExtendedErrorCode: 1555 or 2067
        })
        {
            Context.Entry(user).State = EntityState.Detached;
            return IdentityResult.Failed(new IdentityError
            {
                Code = "DuplicateAccount",
                Description = "The account could not be created."
            });
        }
    }
}
