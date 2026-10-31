using System;
using BuildingManagement.RoomChanges;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomChangeDailyRentQuote_Tests
{
    private static RoomChangeDailyRentQuote Quote(
        DateOnly on, decimal oldRent = 3_000_000m, decimal newRent = 4_000_000m,
        RoomChangeTransferDayChargeOwner day =
            RoomChangeTransferDayChargeOwner.TargetRoom,
        RoomChangeRentRoundingRule rounding =
            RoomChangeRentRoundingRule.NearestVndAwayFromZero)
        => RoomChangeDailyRentQuoteCalculator.Calculate(
            on, oldRent, newRent, day, rounding,
            "SIGNED-TRANSFER-PRICING-REVIEW-001");

    [Fact]
    public void TRF_MID01_Thirty_Day_Month_Splits_Nonoverlapping_Days()
    {
        var q = Quote(new DateOnly(2026, 9, 15));
        Assert.Equal(30, q.CalendarDaysInMonth);
        Assert.Equal(14, q.SourceChargeableDays);
        Assert.Equal(16, q.TargetChargeableDays);
        Assert.Equal(1_400_000m, q.ProposedSourceRentVnd);
        Assert.Equal(2_133_333m, q.ProposedTargetRentVnd);
        Assert.Equal(3_533_333m, q.ProposedCombinedRentVnd);
        Assert.Equal(new DateOnly(2026, 9, 14), q.SourceLastChargeableDate);
        Assert.Equal(new DateOnly(2026, 9, 15), q.TargetFirstChargeableDate);
    }

    [Fact]
    public void TRF_MID02_Explicit_Source_Billed_Transfer_Day_Changes_Both_Cutoffs()
    {
        var q = Quote(new DateOnly(2026, 9, 15),
            day: RoomChangeTransferDayChargeOwner.SourceRoom);
        Assert.Equal(15, q.SourceChargeableDays);
        Assert.Equal(15, q.TargetChargeableDays);
        Assert.Equal(1_500_000m, q.ProposedSourceRentVnd);
        Assert.Equal(2_000_000m, q.ProposedTargetRentVnd);
        Assert.Equal(new DateOnly(2026, 9, 15), q.SourceLastChargeableDate);
        Assert.Equal(new DateOnly(2026, 9, 16), q.TargetFirstChargeableDate);
    }

    [Fact]
    public void TRF_MID03_ThirtyOne_Day_Month_Uses_Actual_Denominator()
    {
        var q = Quote(new DateOnly(2026, 10, 15));
        Assert.Equal(31, q.CalendarDaysInMonth);
        Assert.Equal(14, q.SourceChargeableDays);
        Assert.Equal(17, q.TargetChargeableDays);
        Assert.Equal(1_354_839m, q.ProposedSourceRentVnd);
        Assert.Equal(2_193_548m, q.ProposedTargetRentVnd);
    }

    [Fact]
    public void TRF_MID04_Leap_February_Is_TwentyNine_Days()
    {
        var q = Quote(new DateOnly(2028, 2, 29));
        Assert.Equal(29, q.CalendarDaysInMonth);
        Assert.Equal(28, q.SourceChargeableDays);
        Assert.Equal(1, q.TargetChargeableDays);
        Assert.Equal(29, q.SourceChargeableDays + q.TargetChargeableDays);
    }

    [Fact]
    public void TRF_MID05_First_Day_Target_Ownership_Has_Zero_Source_Days()
    {
        var q = Quote(new DateOnly(2026, 11, 1));
        Assert.Equal(0, q.SourceChargeableDays);
        Assert.Null(q.SourceLastChargeableDate);
        Assert.Equal(4_000_000m, q.ProposedTargetRentVnd);
        Assert.Equal(0m, q.ProposedSourceRentVnd);
    }

    [Fact]
    public void TRF_MID06_Last_Day_Source_Ownership_Matches_Existing_MonthEnd_Lane()
    {
        var q = Quote(new DateOnly(2026, 11, 30),
            day: RoomChangeTransferDayChargeOwner.SourceRoom);
        Assert.Equal(30, q.SourceChargeableDays);
        Assert.Equal(0, q.TargetChargeableDays);
        Assert.Equal(3_000_000m, q.ProposedSourceRentVnd);
        Assert.Equal(0m, q.ProposedTargetRentVnd);
        Assert.Null(q.TargetFirstChargeableDate);
    }

    [Fact]
    public void TRF_MID07_WholeVnd_Rounding_Is_Explicit_And_Changes_Quote()
    {
        var day = new DateOnly(2026, 9, 2);
        var rounded = Quote(day, oldRent: 100m, newRent: 100m);
        var truncated = Quote(day, oldRent: 100m, newRent: 100m,
            rounding: RoomChangeRentRoundingRule.TruncateToWholeVnd);
        Assert.Equal(3m, rounded.ProposedSourceRentVnd);
        Assert.Equal(3m, truncated.ProposedSourceRentVnd);
        Assert.Equal(97m, rounded.ProposedTargetRentVnd);
        Assert.Equal(96m, truncated.ProposedTargetRentVnd);
        Assert.False(rounded.CanIssueInvoice);
        Assert.False(truncated.CanExecuteRoomChange);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(99, 1)]
    public void TRF_MID08_Unspecified_Or_Unknown_Policy_Rule_Is_Denied(
        int day, int rounding)
    {
        Assert.Throws<InvalidOperationException>(() =>
            Quote(new DateOnly(2026, 9, 15),
                day: (RoomChangeTransferDayChargeOwner)day,
                rounding: (RoomChangeRentRoundingRule)rounding));
    }

    [Fact]
    public void TRF_MID09_Blank_Policy_Reference_Or_SubVnd_Rate_Denied()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeDailyRentQuoteCalculator.Calculate(
                new DateOnly(2026, 9, 15), 3_000_000m, 4_000_000m,
                RoomChangeTransferDayChargeOwner.TargetRoom,
                RoomChangeRentRoundingRule.NearestVndAwayFromZero, ""));
        Assert.Throws<InvalidOperationException>(() =>
            Quote(new DateOnly(2026, 9, 15), oldRent: 3_000_000.25m));
        Assert.Throws<InvalidOperationException>(() =>
            Quote(new DateOnly(2026, 9, 15), newRent: -1m));
    }

    [Fact]
    public void TRF_MID10_Preview_Does_Not_Override_Ordinary_Full_Month_MoveOut()
    {
        var q = Quote(new DateOnly(2026, 11, 15));
        Assert.Equal(30, q.SourceChargeableDays + q.TargetChargeableDays);
        Assert.False(q.CanIssueInvoice);
        Assert.False(q.CanExecuteRoomChange);
        // Source SRS section 5 retains FULL final-month rent for normal
        // 2G.5. This preview is explicitly for H2/H3 transfer only.
    }
}
