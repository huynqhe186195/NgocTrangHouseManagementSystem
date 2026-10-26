using System;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Permissions;
using Volo.Abp;
using Volo.Abp.Authorization;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Users;

namespace BuildingManagement.Security;

/// <summary>
/// A building can never be created without a first owner in the same
/// committed transaction. The actor is resolved from the SERVER identity,
/// not from a request DTO or a user-selected owner ID.
/// </summary>
public interface IBuildingOwnerBootstrapPrincipalReader
{
    Task<Guid?> GetAuthorizedIssuerAsync();
}

[ExposeServices(typeof(IBuildingOwnerBootstrapPrincipalReader))]
public sealed class AbpBuildingOwnerBootstrapPrincipalReader :
    IBuildingOwnerBootstrapPrincipalReader, ITransientDependency
{
    private readonly ICurrentUser _user;
    private readonly IPermissionChecker _permissions;

    public AbpBuildingOwnerBootstrapPrincipalReader(
        ICurrentUser user, IPermissionChecker permissions)
    {
        _user = user;
        _permissions = permissions;
    }

    public async Task<Guid?> GetAuthorizedIssuerAsync()
    {
        if (!_user.IsAuthenticated || !_user.Id.HasValue ||
            _user.Id.Value == Guid.Empty)
            return null;

        if (!await _permissions.IsGrantedAsync(
                BuildingManagementPermissions.Buildings.BootstrapOwner))
            return null;
        return _user.Id.Value;
    }
}

/// <summary>
/// Storage writer must insert BOTH Building and the FIRST access grant
/// atomically under a single serializable/locking protocol. No partial
/// building with zero owner is acceptable. A client cannot choose the owner.
/// </summary>
public interface IBuildingOwnerBootstrapWriter
{
    Task<Building> CreateWithOwnerAtomicallyAsync(
        Guid trustedOwnerUserId,
        string name,
        string address,
        string? description);
}

[ExposeServices(typeof(IBuildingOwnerBootstrapWriter))]
public sealed class DenyAllBuildingOwnerBootstrapWriter :
    IBuildingOwnerBootstrapWriter, ITransientDependency
{
    public Task<Building> CreateWithOwnerAtomicallyAsync(
        Guid trustedOwnerUserId,
        string name,
        string address,
        string? description)
        => throw new AbpAuthorizationException(
            "Owner bootstrap is disabled until membership persistence, " +
            "concurrent-writer safety and an audited deployment are approved.");
}

/// <summary>
/// Real BuildingAppService.CreateAsync calls this coordinator now.
/// No unconditional admin bypass, no trusted client owner, and no normal
/// standalone Building insert. Production remains DENY ALL until a
/// reviewed atomic writer is activated after a migration in a test DB.
/// </summary>
[RemoteService(IsEnabled = false)]
public sealed class BuildingOwnerBootstrapCoordinator : ITransientDependency
{
    private readonly IBuildingOwnerBootstrapPrincipalReader _issuers;
    private readonly IBuildingOperatorActiveAccountReader _accounts;
    private readonly IBuildingOwnerBootstrapWriter _writer;

    public BuildingOwnerBootstrapCoordinator(
        IBuildingOwnerBootstrapPrincipalReader issuers,
        IBuildingOperatorActiveAccountReader accounts,
        IBuildingOwnerBootstrapWriter writer)
    {
        _issuers = issuers;
        _accounts = accounts;
        _writer = writer;
    }

    public async Task<Building> CreateWithCurrentOwnerAsync(CreateBuildingDto input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var issuer = await _issuers.GetAuthorizedIssuerAsync();
        if (!issuer.HasValue || issuer.Value == Guid.Empty)
            throw Denied();

        if (!await _accounts.IsActiveAsync(issuer.Value))
            throw Denied();

        // The writer owns BOTH inserts and commit/rollback. No code path
        // creates an unowned building in this coordinator.
        return await _writer.CreateWithOwnerAtomicallyAsync(
            issuer.Value, input.Name, input.Address, input.Description);
    }

    private static AbpAuthorizationException Denied()
        => new("Missing dedicated building owner bootstrap permission " +
               "or inactive authenticated account.");
}
