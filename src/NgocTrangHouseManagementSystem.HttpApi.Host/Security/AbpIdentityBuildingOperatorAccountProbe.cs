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
    private readonly IdentityUserManager _manager;

    public AbpIdentityBuildingOperatorAccountProbe(
        IIdentityUserRepository users,
        IdentityUserManager manager)
    {
        _users = users;
        _manager = manager;
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

        return !await _manager.IsLockedOutAsync(account);
    }
}
