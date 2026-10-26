using Volo.Abp.Reflection;

namespace BuildingManagement.Permissions;

public class BuildingManagementPermissions
{
    public const string GroupName = "BuildingManagement";

    // Permission definitions alone do not grant access. Never expose
    // invoice revision writes before room/building scope is verified.
    public static class Invoices
    {
        public const string RebuildDraft = GroupName + ".Invoices.RebuildDraft";
        public const string ReadRevisionHistory = GroupName + ".Invoices.ReadRevisionHistory";
    }

    public static string[] GetAll()
    {
        return ReflectionHelper.GetPublicConstantsRecursively(typeof(BuildingManagementPermissions));
    }
}
