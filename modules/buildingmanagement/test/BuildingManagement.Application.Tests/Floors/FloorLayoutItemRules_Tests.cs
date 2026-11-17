using Volo.Abp;
using Xunit;

namespace BuildingManagement.Floors;

/// <summary>
/// A wall stores its centreline as JSON, and nothing else may. These tests are
/// pure: they pin the format the designer writes and the reader accepts, so a
/// malformed centreline is rejected at the boundary instead of reaching the
/// plan drawing code, which can only guess at it.
/// </summary>
public sealed class FloorLayoutItemRules_Tests
{
    private static void ValidWall(
        string? points = null,
        decimal? thickness = null,
        decimal? cornerRadius = null)
    {
        FloorLayoutItemRules.Validate(
            FloorLayoutItemKind.Wall, 0, 0, 10, 6,
            points, thickness, cornerRadius);
    }

    private static BusinessException InvalidWall(string? points)
    {
        return Assert.Throws<BusinessException>(() => ValidWall(points));
    }

    [Fact]
    public void WALLGEO01_A_Two_Point_Centreline_Is_The_Minimum_For_A_Straight_Run()
    {
        ValidWall(points: "[{\"x\":0,\"y\":0},{\"x\":10,\"y\":0}]");
        Assert.Equal(
            BuildingManagementErrorCodes.InvalidFloorLayoutItemPoints,
            InvalidWall("[{\"x\":0,\"y\":0}]").Code);
    }

    [Fact]
    public void WALLGEO02_A_Bent_Centreline_Is_Accepted_At_Every_Vertex()
    {
        ValidWall(points:
            "[{\"x\":0,\"y\":0},{\"x\":5,\"y\":0},{\"x\":10,\"y\":6}]");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"x\":0,\"y\":0}")]
    [InlineData("[{\"x\":0},{\"x\":10,\"y\":0}]")]
    [InlineData("[{\"x\":\"0\",\"y\":0},{\"x\":10,\"y\":0}]")]
    [InlineData("[]")]
    [InlineData("[{\"x\":0,\"y\":0},{\"x\":99999,\"y\":0}]")]
    public void WALLGEO03_Malformed_Centreline_Is_Rejected(string points)
    {
        Assert.Equal(
            BuildingManagementErrorCodes.InvalidFloorLayoutItemPoints,
            InvalidWall(points).Code);
    }

    [Fact]
    public void WALLGEO04_More_Than_Sixty_Four_Vertices_Is_Rejected()
    {
        var points = new System.Text.StringBuilder("[");
        for (var i = 0; i < FloorLayoutItemRules.MaxPolylinePoints + 1; i++)
        {
            if (i > 0)
                points.Append(',');
            points.Append("{\"x\":").Append(i).Append(",\"y\":0}");
        }
        points.Append(']');

        Assert.Equal(
            BuildingManagementErrorCodes.InvalidFloorLayoutItemPoints,
            InvalidWall(points.ToString()).Code);
    }

    [Fact]
    public void WALLGEO05_A_Centreline_On_Anything_But_A_Wall_Is_Rejected()
    {
        var error = Assert.Throws<BusinessException>(() =>
            FloorLayoutItemRules.Validate(
                FloorLayoutItemKind.Bed, 0, 0, 2, 1,
                "[{\"x\":0,\"y\":0},{\"x\":2,\"y\":0}]", null, null));

        Assert.Equal(
            BuildingManagementErrorCodes.InvalidFloorLayoutItemPoints, error.Code);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(2.01)]
    public void WALLGEO06_Thickness_Outside_The_Supported_Range_Is_Rejected(
        double thickness)
    {
        var error = Assert.Throws<BusinessException>(() =>
            ValidWall(thickness: (decimal)thickness));

        Assert.Equal(
            BuildingManagementErrorCodes.InvalidFloorLayoutItemPoints, error.Code);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(5.01)]
    public void WALLGEO07_Corner_Radius_Outside_The_Supported_Range_Is_Rejected(
        double cornerRadius)
    {
        var error = Assert.Throws<BusinessException>(() =>
            ValidWall(cornerRadius: (decimal)cornerRadius));

        Assert.Equal(
            BuildingManagementErrorCodes.InvalidFloorLayoutItemPoints, error.Code);
    }

    [Fact]
    public void WALLGEO08_A_Wall_Saved_Before_Centrelines_Still_Saves()
    {
        // Every wall drawn before this feature has a box and nothing else. It
        // must keep round-tripping, or opening an old floor would fail to save.
        ValidWall();
        Assert.Null(FloorLayoutItemRules.NormalizePoints("   "));
    }

    [Fact]
    public void WALLGEO09_Blank_Points_Normalise_To_Null_And_Real_Ones_Survive()
    {
        Assert.Null(FloorLayoutItemRules.NormalizePoints(null));
        Assert.Null(FloorLayoutItemRules.NormalizePoints(""));
        Assert.Equal(
            "[{\"x\":0,\"y\":0},{\"x\":10,\"y\":0}]",
            FloorLayoutItemRules.NormalizePoints(
                "  [{\"x\":0,\"y\":0},{\"x\":10,\"y\":0}]  "));
    }
}
