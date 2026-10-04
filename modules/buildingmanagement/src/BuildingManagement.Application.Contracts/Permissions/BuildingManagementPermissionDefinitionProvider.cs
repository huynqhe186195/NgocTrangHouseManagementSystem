using BuildingManagement.Localization;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Localization;

namespace BuildingManagement.Permissions;

public class BuildingManagementPermissionDefinitionProvider : PermissionDefinitionProvider
{
    public override void Define(IPermissionDefinitionContext context)
    {
        var myGroup = context.AddGroup(BuildingManagementPermissions.GroupName, L("Permission:BuildingManagement"));
    }

    private static LocalizableString L(string name)
    {
        return LocalizableString.Create<BuildingManagementResource>(name);
    }
}
