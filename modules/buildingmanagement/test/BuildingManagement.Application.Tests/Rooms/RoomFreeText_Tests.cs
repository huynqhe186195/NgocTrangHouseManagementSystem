using Xunit;

namespace BuildingManagement.Rooms;

/// <summary>
/// "Chuẩn hóa loại phòng" is about the spelling of the owner's own words, not
/// about replacing them with a catalogue: the entity keeps RoomType as free
/// text on purpose, so what a grouping or a search needs is only that the same
/// phrase typed twice compares equal. These tests pin that contract, and the
/// same rule covers the location note, which is free text for the same reason.
/// </summary>
public sealed class RoomFreeText_Tests
{
    [Fact]
    public void ROOMFREE01_Surrounding_And_Doubled_Space_Collapses()
    {
        Assert.Equal(
            "Phòng tiêu chuẩn",
            RoomFreeText.Normalize("   Phòng   tiêu    chuẩn  "));
    }

    [Fact]
    public void ROOMFREE02_Tabs_And_Newlines_Count_As_Space()
    {
        Assert.Equal(
            "Gần cầu thang, view nội khu",
            RoomFreeText.Normalize("Gần\tcầu\nthang,\r\nview  nội khu"));
    }

    [Fact]
    public void ROOMFREE03_Case_And_Wording_Are_Left_Exactly_As_Typed()
    {
        Assert.Equal("Phòng VIP", RoomFreeText.Normalize("  Phòng VIP "));
    }

    [Fact]
    public void ROOMFREE04_Decomposed_Vietnamese_Becomes_One_Comparable_Form()
    {
        // "ệ" as a base letter plus a combining circumflex and dot below, which
        // is what some keyboards and clipboard sources produce.
        var decomposed = "Phòng tiêu chuẩn";

        Assert.Equal("Phòng tiêu chuẩn", RoomFreeText.Normalize(decomposed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void ROOMFREE05_Blank_Is_Stored_As_No_Value(string? value)
    {
        Assert.Null(RoomFreeText.Normalize(value));
    }
}
