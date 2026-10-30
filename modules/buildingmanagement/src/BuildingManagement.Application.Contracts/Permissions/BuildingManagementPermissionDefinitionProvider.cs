using BuildingManagement.Localization;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Localization;

namespace BuildingManagement.Permissions;

public class BuildingManagementPermissionDefinitionProvider : PermissionDefinitionProvider
{
    public override void Define(IPermissionDefinitionContext context)
    {
        var myGroup = context.AddGroup(BuildingManagementPermissions.GroupName, L("Permission:BuildingManagement"));

        // Defining a permission does not assign it to any user or role.
        // A future mutating endpoint must check the permission AND the
        // authenticated operator's server-resolved building/room scope.
        // These names form a capability catalogue only. There is no
        // operator-building assignment store or CRUD enforcement yet.
        myGroup.AddPermission(BuildingManagementPermissions.Buildings.Read, L("Permission:Buildings.Read"));
        myGroup.AddPermission(BuildingManagementPermissions.Buildings.Manage, L("Permission:Buildings.Manage"));
        // Never automatically grant via a generic building manager role.
        myGroup.AddPermission(BuildingManagementPermissions.Buildings.AssignOperators, L("Permission:Buildings.AssignOperators"));
        myGroup.AddPermission(BuildingManagementPermissions.Buildings.RevokeOperators, L("Permission:Buildings.RevokeOperators"));
        myGroup.AddPermission(BuildingManagementPermissions.Buildings.BootstrapOwner, L("Permission:Buildings.BootstrapOwner"));
        myGroup.AddPermission(BuildingManagementPermissions.Buildings.CoSignOwnerRecovery, L("Permission:Buildings.CoSignOwnerRecovery"));
        myGroup.AddPermission(BuildingManagementPermissions.Floors.Read, L("Permission:Floors.Read"));
        myGroup.AddPermission(BuildingManagementPermissions.Floors.Manage, L("Permission:Floors.Manage"));
        myGroup.AddPermission(BuildingManagementPermissions.Rooms.Read, L("Permission:Rooms.Read"));
        myGroup.AddPermission(BuildingManagementPermissions.Rooms.Manage, L("Permission:Rooms.Manage"));
        myGroup.AddPermission(BuildingManagementPermissions.Contracts.Read, L("Permission:Contracts.Read"));
        myGroup.AddPermission(BuildingManagementPermissions.Contracts.Manage, L("Permission:Contracts.Manage"));
        // Independently persisted utility permissions, not implied by
        // Contracts.Manage, Rooms.Manage or root membership alone.
        myGroup.AddPermission(BuildingManagementPermissions.Utilities.Read, L("Permission:Utilities.Read"));
        myGroup.AddPermission(BuildingManagementPermissions.Utilities.Manage, L("Permission:Utilities.Manage"));

        myGroup.AddPermission(
            BuildingManagementPermissions.Invoices.RebuildDraft,
            L("Permission:Invoices.RebuildDraft"));
        myGroup.AddPermission(
            BuildingManagementPermissions.Invoices.ReadRevisionHistory,
            L("Permission:Invoices.ReadRevisionHistory"));
    }

    private static LocalizableString L(string name)
    {
        return LocalizableString.Create<BuildingManagementResource>(name);
    }
}
