using Volo.Abp.Reflection;

namespace BuildingManagement.Permissions;

public class BuildingManagementPermissions
{
    public const string GroupName = "BuildingManagement";

    public static string[] GetAll()
    {
        return ReflectionHelper.GetPublicConstantsRecursively(typeof(BuildingManagementPermissions));
    }
}
