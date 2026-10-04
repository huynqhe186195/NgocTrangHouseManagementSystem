using NgocTrangHouseManagementSystem.Localization;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Localization;
using Volo.Abp.MultiTenancy;

namespace NgocTrangHouseManagementSystem.Permissions;

public class NgocTrangHouseManagementSystemPermissionDefinitionProvider : PermissionDefinitionProvider
{
    public override void Define(IPermissionDefinitionContext context)
    {
        var myGroup = context.AddGroup(NgocTrangHouseManagementSystemPermissions.GroupName);

        //Define your own permissions here. Example:
        //myGroup.AddPermission(NgocTrangHouseManagementSystemPermissions.MyPermission1, L("Permission:MyPermission1"));
    }

    private static LocalizableString L(string name)
    {
        return LocalizableString.Create<NgocTrangHouseManagementSystemResource>(name);
    }
}
