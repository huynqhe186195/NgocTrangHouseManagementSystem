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
        myGroup.AddPermission(BuildingManagementPermissions.Floors.Read, L("Permission:Floors.Read"));
        myGroup.AddPermission(BuildingManagementPermissions.Floors.Manage, L("Permission:Floors.Manage"));
        myGroup.AddPermission(BuildingManagementPermissions.Rooms.Read, L("Permission:Rooms.Read"));
        myGroup.AddPermission(BuildingManagementPermissions.Rooms.Manage, L("Permission:Rooms.Manage"));
        myGroup.AddPermission(BuildingManagementPermissions.Contracts.Read, L("Permission:Contracts.Read"));
        myGroup.AddPermission(BuildingManagementPermissions.Contracts.Manage, L("Permission:Contracts.Manage"));

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
