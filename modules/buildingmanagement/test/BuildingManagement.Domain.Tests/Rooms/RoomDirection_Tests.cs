using System;
using BuildingManagement.Rooms;
using Xunit;

namespace BuildingManagement.DomainTests.Rooms;

public sealed class RoomDirection_Tests
{
    [Fact]
    public void DIR_01_Enum_Values_Are_Stable()
    {
        // Integers are persisted, so a shift would silently turn every stored
        // "Đông Nam" into a different compass point.
        Assert.Equal(1, (int)RoomDirection.North);
        Assert.Equal(2, (int)RoomDirection.NorthEast);
        Assert.Equal(3, (int)RoomDirection.East);
        Assert.Equal(4, (int)RoomDirection.SouthEast);
        Assert.Equal(5, (int)RoomDirection.South);
        Assert.Equal(6, (int)RoomDirection.SouthWest);
        Assert.Equal(7, (int)RoomDirection.West);
        Assert.Equal(8, (int)RoomDirection.NorthWest);
    }

    [Fact]
    public void DIR_02_Eight_Points_No_More()
    {
        Assert.Equal(8, Enum.GetValues<RoomDirection>().Length);
    }
}
