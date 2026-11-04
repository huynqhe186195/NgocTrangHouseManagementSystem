using System;
using BuildingManagement.RoomChanges;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomChangeApprovedH2H3ExecutionPolicy_Tests
{
    private static RoomChangeDailyRentQuote Quote(
        DateOnly date,
        RoomChangeTransferDayChargeOwner owner =
            RoomChangeTransferDayChargeOwner.TargetRoom,
        RoomChangeRentRoundingRule rounding =
            RoomChangeRentRoundingRule.NearestVndAwayFromZero)
        => RoomChangeDailyRentQuoteCalculator.Calculate(
            date, 3_000_000m, 4_000_000m,
            owner, rounding, "OWNER-SIGNED-TRANSFER-DAY-20261104");

    [Fact]
    public void TRF_D01_01_Thirty_Day_Month_Old14_New16()
    {
        var q = Quote(new DateOnly(2026, 11, 15));
        RoomChangeApprovedH2H3ExecutionPolicy.RequireExecutableQuote(q);
        Assert.Equal(14, q.SourceChargeableDays);
        Assert.Equal(16, q.TargetChargeableDays);
        Assert.Equal(new DateOnly(2026, 11, 15), q.TargetFirstChargeableDate);
    }

    [Fact]
    public void TRF_D01_02_ThirtyOne_Day_Month_Old14_New17()
    {
        var q = Quote(new DateOnly(2026, 12, 15));
        RoomChangeApprovedH2H3ExecutionPolicy.RequireExecutableQuote(q);
        Assert.Equal(14, q.SourceChargeableDays);
        Assert.Equal(17, q.TargetChargeableDays);
    }

    [Fact]
    public void TRF_D01_03_Leap_Year_Last_Day_Belongs_To_New_Room()
    {
        var q = Quote(new DateOnly(2028, 2, 29));
        RoomChangeApprovedH2H3ExecutionPolicy.RequireExecutableQuote(q);
        Assert.Equal(28, q.SourceChargeableDays);
        Assert.Equal(1, q.TargetChargeableDays);
        Assert.Equal(new DateOnly(2028, 2, 28), q.SourceLastChargeableDate);
    }

    [Fact]
    public void TRF_D01_04_First_And_Last_Day_Have_No_Double_Billing()
    {
        foreach (var day in new[] {
            new DateOnly(2026, 11, 1),
            new DateOnly(2026, 11, 30)
        })
        {
            var q = Quote(day);
            RoomChangeApprovedH2H3ExecutionPolicy.RequireExecutableQuote(q);
            Assert.Equal(30, q.SourceChargeableDays + q.TargetChargeableDays);
            Assert.Equal(day, q.TargetFirstChargeableDate);
        }
    }

    [Fact]
    public void TRF_D01_05_Legacy_SourceDay_Quote_Cannot_Be_Executed()
    {
        var q = Quote(new DateOnly(2026, 11, 15),
            RoomChangeTransferDayChargeOwner.SourceRoom);
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeApprovedH2H3ExecutionPolicy.RequireExecutableQuote(q));
    }

    [Fact]
    public void TRF_D01_06_Legacy_Truncation_Cannot_Be_Executed()
    {
        var q = Quote(new DateOnly(2026, 11, 15),
            rounding: RoomChangeRentRoundingRule.TruncateToWholeVnd);
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeApprovedH2H3ExecutionPolicy.RequireExecutableQuote(q));
    }

    [Fact]
    public void TRF_D01_07_Forged_Boundaries_Or_Amounts_Cannot_Be_Executed()
    {
        var q = Quote(new DateOnly(2026, 11, 15));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeApprovedH2H3ExecutionPolicy.RequireExecutableQuote(
                q with { SourceChargeableDays = 15 }));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeApprovedH2H3ExecutionPolicy.RequireExecutableQuote(
                q with { ProposedTargetRentVnd = 4_000_000m }));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeApprovedH2H3ExecutionPolicy.RequireExecutableQuote(
                q with { TargetFirstChargeableDate = new DateOnly(2026, 11, 16) }));
    }
    [Fact]
    public void TRF_D01_08_Exactly_Half_Vnd_Rounds_Up_On_Both_Rooms()
    {
        var q = RoomChangeDailyRentQuoteCalculator.Calculate(
            new DateOnly(2026, 11, 16), 45m, 45m,
            RoomChangeTransferDayChargeOwner.TargetRoom,
            RoomChangeRentRoundingRule.NearestVndAwayFromZero,
            "OWNER-SIGNED-ROUNDING-EXACT-HALF-20261104");
        RoomChangeApprovedH2H3ExecutionPolicy.RequireExecutableQuote(q);
        Assert.Equal(15, q.SourceChargeableDays);
        Assert.Equal(15, q.TargetChargeableDays);
        Assert.Equal(23m, q.ProposedSourceRentVnd);
        Assert.Equal(23m, q.ProposedTargetRentVnd);
    }

}
