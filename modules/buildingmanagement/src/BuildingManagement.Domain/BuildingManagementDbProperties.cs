namespace BuildingManagement;

public static class BuildingManagementDbProperties
{
    public static string DbTablePrefix { get; set; } = "BuildingManagement";

    public static string? DbSchema { get; set; } = null;

    public const string ConnectionStringName = "BuildingManagement";
}
