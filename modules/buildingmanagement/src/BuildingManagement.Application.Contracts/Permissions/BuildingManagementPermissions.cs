using Volo.Abp.Reflection;

namespace BuildingManagement.Permissions;

public class BuildingManagementPermissions
{
    public const string GroupName = "BuildingManagement";

    // Permission definitions alone do not grant access. Never expose
    // invoice revision writes before room/building scope is verified.
    // Future server-side resource grants. Declaring these does not assign
    // users to buildings or authorize a controller operation by itself.
    public static class Buildings
    {
        public const string Read = GroupName + ".Buildings.Read";
        public const string Manage = GroupName + ".Buildings.Manage";
        // Separate privileged operations. ManageBuilding is NOT sufficient.
        public const string AssignOperators = GroupName + ".Buildings.AssignOperators";
        public const string RevokeOperators = GroupName + ".Buildings.RevokeOperators";
        // Never assigned automatically. Dedicated, audited first-owner provisioning only.
        public const string BootstrapOwner = GroupName + ".Buildings.BootstrapOwner";
    }

    public static class Floors
    {
        public const string Read = GroupName + ".Floors.Read";
        public const string Manage = GroupName + ".Floors.Manage";
    }

    public static class Rooms
    {
        public const string Read = GroupName + ".Rooms.Read";
        public const string Manage = GroupName + ".Rooms.Manage";
    }

    public static class Contracts
    {
        public const string Read = GroupName + ".Contracts.Read";
        public const string Manage = GroupName + ".Contracts.Manage";
    }

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
