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
        // 118 daily cases in a single xUnit Fact to preserve exact CI counts.
        foreach (var first in new[] {
            new DateOnly(2026, 2, 1), // nonleap: 28 days
            new DateOnly(2028, 2, 1), // leap: 29 days
            new DateOnly(2026, 4, 1), // 30 days
            new DateOnly(2026, 1, 1)  // 31 days
        })
        {
            var days = DateTime.DaysInMonth(first.Year, first.Month);
            for (var d = 1; d <= days; d++)
            {
                var handover = new DateOnly(first.Year, first.Month, d);
                var quote = Quote(handover);
                RoomChangeApprovedH2H3ExecutionPolicy.RequireExecutableQuote(quote);

                Assert.Equal(days, quote.CalendarDaysInMonth);
                Assert.Equal(d - 1, quote.SourceChargeableDays);
                Assert.Equal(days - d + 1, quote.TargetChargeableDays);
                Assert.Equal(days,
                    quote.SourceChargeableDays + quote.TargetChargeableDays);
                Assert.Equal(d == 1 ? (DateOnly?)null : handover.AddDays(-1),
                    quote.SourceLastChargeableDate);
                Assert.Equal((DateOnly?)handover,
                    quote.TargetFirstChargeableDate);

                var expectedSource = decimal.Round(
                    3_000_000m * (d - 1) / days, 0,
                    MidpointRounding.AwayFromZero);
                var expectedTarget = decimal.Round(
                    4_000_000m * (days - d + 1) / days, 0,
                    MidpointRounding.AwayFromZero);
                Assert.Equal(expectedSource, quote.ProposedSourceRentVnd);
                Assert.Equal(expectedTarget, quote.ProposedTargetRentVnd);
                Assert.Equal(expectedSource + expectedTarget,
                    quote.ProposedCombinedRentVnd);
                Assert.False(quote.CanIssueInvoice);
                Assert.False(quote.CanExecuteRoomChange);
            }
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
        // Independent 22.5-VND halves must each round to 23 VND
        // in both 28- and 30-day months (separate rent lines).
        foreach (var handover in new[] {
            new DateOnly(2026, 2, 15), // nonleap February: 14 / 14
            new DateOnly(2026, 4, 16)  // April: 15 / 15
        })
        {
            var q = RoomChangeDailyRentQuoteCalculator.Calculate(
                handover, 45m, 45m,
                RoomChangeTransferDayChargeOwner.TargetRoom,
                RoomChangeRentRoundingRule.NearestVndAwayFromZero,
                "OWNER-SIGNED-ROUNDING-EXACT-HALF-20261104");
            RoomChangeApprovedH2H3ExecutionPolicy.RequireExecutableQuote(q);
            Assert.Equal(23m, q.ProposedSourceRentVnd);
            Assert.Equal(23m, q.ProposedTargetRentVnd);
            Assert.Equal(46m, q.ProposedCombinedRentVnd);
        }

        // For odd denominators, compare independent expected values
        // rather than assuming an impossible exact half for integer rent.
        foreach (var handover in new[] {
            new DateOnly(2028, 2, 15), // leap February: 14 / 15
            new DateOnly(2026, 1, 16)  // January: 15 / 16
        })
        {
            var q = RoomChangeDailyRentQuoteCalculator.Calculate(
                handover, 45m, 45m,
                RoomChangeTransferDayChargeOwner.TargetRoom,
                RoomChangeRentRoundingRule.NearestVndAwayFromZero,
                "OWNER-SIGNED-ROUNDING-ODD-MONTH-20261104");
            RoomChangeApprovedH2H3ExecutionPolicy.RequireExecutableQuote(q);
            var days = DateTime.DaysInMonth(handover.Year, handover.Month);
            Assert.Equal(decimal.Round(45m * q.SourceChargeableDays /
                days, 0, MidpointRounding.AwayFromZero),
                q.ProposedSourceRentVnd);
            Assert.Equal(decimal.Round(45m * q.TargetChargeableDays /
                days, 0, MidpointRounding.AwayFromZero),
                q.ProposedTargetRentVnd);
        }
    }

}
