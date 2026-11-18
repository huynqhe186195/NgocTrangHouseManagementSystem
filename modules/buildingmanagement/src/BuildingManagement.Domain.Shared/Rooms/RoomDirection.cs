namespace BuildingManagement.Rooms;

/// <summary>
/// The eight compass points a letting advert names. Stored as an enum rather
/// than free text so the label shown in the UI is one agreed string, and so a
/// filter can compare values instead of guessing at spelling.
/// </summary>
public enum RoomDirection
{
    North = 1,
    NorthEast = 2,
    East = 3,
    SouthEast = 4,
    South = 5,
    SouthWest = 6,
    West = 7,
    NorthWest = 8
}
