namespace BuildingManagement;

public static class BuildingManagementErrorCodes
{
    public const string InvalidRoomNumber =
        "BuildingManagement:InvalidRoomNumber";

    public const string RoomNumberDoesNotMatchFloor =
        "BuildingManagement:RoomNumberDoesNotMatchFloor";

    public const string RoomNumberAlreadyExists =
        "BuildingManagement:RoomNumberAlreadyExists";

    public const string FloorNumberAlreadyExists =
        "BuildingManagement:FloorNumberAlreadyExists";

    public const string BuildingNotFound =
        "BuildingManagement:BuildingNotFound";

    public const string BuildingHasFloors =
        "BuildingManagement:BuildingHasFloors";

    public const string FloorHasRooms =
        "BuildingManagement:FloorHasRooms";

    public const string FloorNotFound =
        "BuildingManagement:FloorNotFound";

    public const string RoomNotFound =
        "BuildingManagement:RoomNotFound";
} // Consolidate all of the module's error codes in one place.
