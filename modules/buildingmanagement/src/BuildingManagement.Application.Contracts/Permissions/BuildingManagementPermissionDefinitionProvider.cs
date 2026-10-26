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
