using System;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// OI-07 / H2-H3 READ-ONLY candidate calculator for one Vietnam calendar
/// month. The owner approved daily allocation as an approach, but NOT the
/// transfer-day owner or VND rounding method. Both MUST be supplied explicitly
/// from a separately signed rule; no implicit defaults are permitted.
/// Quotes are not invoices, payment obligations or a 2H transfer permission.
/// Ordinary 2G.5 final-month rent policy remains full-month.
/// </summary>
public static class RoomChangeDailyRentQuoteCalculator
{
    public static RoomChangeDailyRentQuote Calculate(
        DateOnly transferOn,
        decimal sourceMonthlyRentVnd,
        decimal targetMonthlyRentVnd,
        RoomChangeTransferDayChargeOwner transferDayOwner,
        RoomChangeRentRoundingRule rounding,
        string? separatelySignedRuleReference)
    {
        if (transferOn == default || transferOn.Year is < 2000 or > 2200 ||
            sourceMonthlyRentVnd <= 0m ||
            targetMonthlyRentVnd <= 0m ||
            decimal.Truncate(sourceMonthlyRentVnd) != sourceMonthlyRentVnd ||
            decimal.Truncate(targetMonthlyRentVnd) != targetMonthlyRentVnd ||
            !Enum.IsDefined(transferDayOwner) ||
            !Enum.IsDefined(rounding) ||
            string.IsNullOrWhiteSpace(separatelySignedRuleReference) ||
            separatelySignedRuleReference.Trim().Length is < 12 or > 500)
            throw new InvalidOperationException(
                "Explicit signed transfer-day and whole-VND pricing rules are required for a quote.");

        var monthDays = DateTime.DaysInMonth(transferOn.Year, transferOn.Month);
        var sourceDays = transferDayOwner switch
        {
            RoomChangeTransferDayChargeOwner.SourceRoom => transferOn.Day,
            RoomChangeTransferDayChargeOwner.TargetRoom => transferOn.Day - 1,
            _ => throw new InvalidOperationException("Transfer-day owner is unspecified.")
        };
        var targetDays = monthDays - sourceDays;
        if (sourceDays < 0 || targetDays < 0 ||
            sourceDays + targetDays != monthDays)
            throw new InvalidOperationException("Transfer dates must partition the month exactly once.");

        decimal Prorate(decimal rent, int occupied)
        {
            var unrounded = checked(rent * occupied / monthDays);
            return rounding switch
            {
                RoomChangeRentRoundingRule.NearestVndAwayFromZero =>
                    decimal.Round(unrounded, 0, MidpointRounding.AwayFromZero),
                RoomChangeRentRoundingRule.TruncateToWholeVnd =>
                    decimal.Truncate(unrounded),
                _ => throw new InvalidOperationException("Unapproved rounding rule.")
            };
        }

        var oldAmount = Prorate(sourceMonthlyRentVnd, sourceDays);
        var newAmount = Prorate(targetMonthlyRentVnd, targetDays);
        var sourceLastDate = sourceDays == 0 ? (DateOnly?)null :
            new DateOnly(transferOn.Year, transferOn.Month, sourceDays);
        var targetFirstDate = targetDays == 0 ? (DateOnly?)null :
            new DateOnly(transferOn.Year, transferOn.Month, sourceDays + 1);

        return new RoomChangeDailyRentQuote(
            transferOn, monthDays, sourceDays, targetDays,
            sourceMonthlyRentVnd, targetMonthlyRentVnd,
            oldAmount, newAmount, checked(oldAmount + newAmount),
            sourceLastDate, targetFirstDate,
            transferDayOwner, rounding, separatelySignedRuleReference.Trim());
    }
}

public enum RoomChangeTransferDayChargeOwner
{
    SourceRoom = 1,
    TargetRoom = 2
}

public enum RoomChangeRentRoundingRule
{
    NearestVndAwayFromZero = 1,
    TruncateToWholeVnd = 2
}

/// <summary>
/// Hypothetical rent-only figures, no electricity/water or fixed fees.
/// Policy reference is a TRACE, not independent signature validation.
/// This does not attest real occupancy/handover, approve a tariff or permit
/// an invoice correction/payment/contract end.
/// </summary>
public sealed record RoomChangeDailyRentQuote(
    DateOnly TransferDate,
    int CalendarDaysInMonth,
    int SourceChargeableDays,
    int TargetChargeableDays,
    decimal SourceFullMonthlyRentVnd,
    decimal TargetFullMonthlyRentVnd,
    decimal ProposedSourceRentVnd,
    decimal ProposedTargetRentVnd,
    decimal ProposedCombinedRentVnd,
    DateOnly? SourceLastChargeableDate,
    DateOnly? TargetFirstChargeableDate,
    RoomChangeTransferDayChargeOwner TransferDayOwner,
    RoomChangeRentRoundingRule Rounding,
    string SignedRuleReference)
{
    public bool CanIssueInvoice => false;
    public bool CanExecuteRoomChange => false;
}
