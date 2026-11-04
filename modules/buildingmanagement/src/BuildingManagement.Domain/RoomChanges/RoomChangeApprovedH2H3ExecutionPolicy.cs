using System;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// Binding D01/D02 execution policy approved by the project Owner
/// on 2026-11-04. Historical candidates may preserve another day/rounding
/// choice, but a NEW executable transfer must bill the handover Vietnam
/// civil date to the target room and use midpoint-away-from-zero VND rounding.
/// This validator is NOT an invoice writer or signature authenticator.
/// </summary>
public static class RoomChangeApprovedH2H3ExecutionPolicy
{
    public static void RequireExecutableQuote(RoomChangeDailyRentQuote? quote)
    {
        if (quote is null || quote.TransferDate == default ||
            quote.TransferDayOwner != RoomChangeTransferDayChargeOwner.TargetRoom ||
            quote.Rounding != RoomChangeRentRoundingRule.NearestVndAwayFromZero)
            throw new InvalidOperationException(
                "New Phase 2H transfers require target-room handover-day ownership and whole-VND midpoint-up rounding.");

        var expected = RoomChangeDailyRentQuoteCalculator.Calculate(
            quote.TransferDate, quote.SourceFullMonthlyRentVnd,
            quote.TargetFullMonthlyRentVnd, quote.TransferDayOwner,
            quote.Rounding, quote.SignedRuleReference);

        if (quote != expected ||
            quote.SourceChargeableDays != quote.TransferDate.Day - 1 ||
            quote.TargetFirstChargeableDate != quote.TransferDate ||
            quote.CalendarDaysInMonth != quote.SourceChargeableDays +
                quote.TargetChargeableDays)
            throw new InvalidOperationException(
                "Transfer must partition the Vietnam billing month with no duplicate handover day.");
    }

    /// <summary>
    /// Call only AFTER verifying the signed original candidate with
    /// rule.Verify(request, source, roster, policy, review). This check
    /// does not by itself prove Owner signature or database origin.
    /// </summary>
    public static void RequireExecutableRule(
        RoomChangeDayPricingRuleCandidate? rule, DateOnly transferDate)
    {
        if (rule is null || rule.TransferDate != transferDate)
            throw new InvalidOperationException(
                "The original pricing rule must belong to the transfer date.");

        var quote = RoomChangeDailyRentQuoteCalculator.Calculate(
            rule.TransferDate, rule.SourceMonthlyRentVnd,
            rule.TargetMonthlyRentVnd, rule.TransferDayOwner,
            rule.Rounding, rule.SignedPricingRuleReference);
        RequireExecutableQuote(quote);

        if (rule.CalendarDays != quote.CalendarDaysInMonth ||
            rule.SourceChargeableDays != quote.SourceChargeableDays ||
            rule.TargetChargeableDays != quote.TargetChargeableDays ||
            rule.ProposedSourceRentVnd != quote.ProposedSourceRentVnd ||
            rule.ProposedTargetRentVnd != quote.ProposedTargetRentVnd)
            throw new InvalidOperationException(
                "Original signed day-proration fields do not match the approved execution policy.");
    }
}
