using System;
using BuildingManagement.MoveOuts;
using Xunit;

namespace BuildingManagement.MoveOuts.Tests;

public sealed class MoveOutNoticePolicy_Tests
{
    [Theory]
    [InlineData(2026, 10, 22, 2026, 11, 30)]
    [InlineData(2026, 1, 31, 2026, 2, 28)]
    [InlineData(2028, 1, 31, 2028, 2, 29)]
    [InlineData(2026, 2, 28, 2026, 3, 31)]
    public void Earliest_Is_One_Calendar_Month_Then_MonthEnd(
        int y, int m, int d, int ey, int em, int ed)
    {
        var actual = MoveOutNoticePolicy.EarliestPermittedMoveOutDate(new DateOnly(y, m, d));
        Assert.Equal(new DateOnly(ey, em, ed), actual);
    }

    [Fact]
    public void Notice_Requires_Actual_MonthEnd_And_Enough_Time()
    {
        var submitted = new DateOnly(2026, 10, 22);
        Assert.False(MoveOutNoticePolicy.IsValid(submitted, new DateOnly(2026, 10, 31)));
        Assert.False(MoveOutNoticePolicy.IsValid(submitted, new DateOnly(2026, 11, 15)));
        Assert.True(MoveOutNoticePolicy.IsValid(submitted, new DateOnly(2026, 11, 30)));
        Assert.True(MoveOutNoticePolicy.IsValid(submitted, new DateOnly(2026, 12, 31)));
    }

    [Fact]
    public void Use_Vietnam_Date_Not_Utc_Date_Around_Midnight()
    {
        var utc = new DateTime(2026, 10, 22, 18, 30, 0, DateTimeKind.Utc);
        Assert.Equal(new DateOnly(2026, 10, 23), MoveOutNoticePolicy.VietnamDate(utc));
    }
}
