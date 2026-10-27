using System;
using System.Threading.Tasks;
using BuildingManagement.Security;
using Volo.Abp.Identity;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Unregistered ABP Identity adapter candidate for the grant-authority
/// RFC. Deliberately does NOT implement ITransientDependency or
/// ExposeServices: production still resolves the deny-all account reader.
/// Do not register until both the privilege/ownership model and membership
/// persistence have been separately reviewed and integrated.
/// </summary>
public sealed class AbpIdentityBuildingOperatorAccountProbe :
    IBuildingOperatorActiveAccountReader
{
    private readonly IIdentityUserRepository _users;
    private readonly Func<IdentityUser, Task<bool>> _isLockedOut;

    // The public candidate always delegates lockout checks to ABP's
    // IdentityUserManager. Production does NOT register this candidate.
    public AbpIdentityBuildingOperatorAccountProbe(
        IIdentityUserRepository users,
        IdentityUserManager manager)
        : this(users, (manager ?? throw new ArgumentNullException(nameof(manager)))
            .IsLockedOutAsync)
    {
    }

    // Only the isolated test assembly can exercise the inherited Identity
    // UserManager lockout semantics without a production Identity database.
    internal AbpIdentityBuildingOperatorAccountProbe(
        IIdentityUserRepository users,
        Func<IdentityUser, Task<bool>> isLockedOut)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _isLockedOut = isLockedOut ??
            throw new ArgumentNullException(nameof(isLockedOut));
    }

    public async Task<bool> IsActiveAsync(Guid userId)
    {
        if (userId == Guid.Empty)
            return false;

        // Normal ABP identity/tenant filters are intentionally retained.
        // A missing/soft-deleted/disabled/locked account is not eligible
        // for a newly issued building membership.
        var account = await _users.FindAsync(userId);
        if (account is null || !account.IsActive)
            return false;

        return !await _isLockedOut(account);
    }
}
