using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using BuildingManagement.Invoices;
using BuildingManagement.RoomChanges;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.Domain.Tests.RoomChanges;

/// <summary>
/// P2.1 — the D01/D02 daily-rent policy applied to REAL issued invoices.
/// Every correction is an append-only proposal; the issued invoice, its meter
/// evidence and its receipts are never rewritten by this planner.
/// </summary>
public sealed class RoomChangeIssuedInvoiceCorrectionPlanner_Tests
{
    private const string Rule = "D01-D02-SIGNED-TRANSFER-RULE-2026-11";
    private const string Reason =
        "D01/D02 transfer day allocation corrects the issued rent lines";
    private const string Evidence = "ROOM-CHANGE-HANDOVER-ACK-2026-11-01";

    private static readonly Guid SourceContract = Guid.NewGuid();
    private static readonly Guid TargetContract = Guid.NewGuid();
    private static readonly Guid SourceBuilding = Guid.NewGuid();
    private static readonly Guid TargetBuilding = Guid.NewGuid();

    // ---- D01/D02 policy parity against an independent rational oracle ----

    [Fact]
    public void RICC01_Target_Owns_Handover_Day_Across_Every_Month_Length()
    {
        var lengths = new[]
        {
            (Year: 2027, Month: 2, Days: 28),
            (Year: 2028, Month: 2, Days: 29), // leap year
            (Year: 2027, Month: 4, Days: 30),
            (Year: 2027, Month: 1, Days: 31)
        };
        var scenarios = 0;

        foreach (var (year, month, daysInMonth) in lengths)
        {
            Assert.Equal(daysInMonth,
                DateTime.DaysInMonth(year, month));

            for (var transferDay = 1; transferDay <= daysInMonth; transferDay++)
            {
                var transferOn = new DateOnly(year, month, transferDay);
                var sourceRent = 2_000_000m + transferDay * 1_000m;
                var targetRent = 3_000_000m + transferDay * 3_000m;

                var quote = RoomChangeDailyRentQuoteCalculator.Calculate(
                    transferOn, sourceRent, targetRent,
                    RoomChangeTransferDayChargeOwner.TargetRoom,
                    RoomChangeRentRoundingRule.NearestVndAwayFromZero, Rule);

                // D01: the target room owns the handover day, so the source
                // is charged only through the day before.
                Assert.Equal(transferDay - 1, quote.SourceChargeableDays);
                Assert.Equal(daysInMonth - (transferDay - 1),
                    quote.TargetChargeableDays);
                Assert.Equal(daysInMonth,
                    quote.SourceChargeableDays + quote.TargetChargeableDays);

                Assert.Equal(
                    OracleProrate(sourceRent, transferDay - 1, daysInMonth),
                    quote.ProposedSourceRentVnd);
                Assert.Equal(
                    OracleProrate(targetRent, daysInMonth - transferDay + 1,
                        daysInMonth),
                    quote.ProposedTargetRentVnd);
                Assert.Equal(
                    quote.ProposedSourceRentVnd + quote.ProposedTargetRentVnd,
                    quote.ProposedCombinedRentVnd);
                scenarios++;
            }
        }

        Assert.Equal(118, scenarios);
    }

    [Fact]
    public void RICC02_Source_Ownership_Variant_Is_Exactly_One_Day_More_For_Source()
    {
        // The calculator's other approved variant must stay available and
        // must differ by exactly one source day, never by a silent default.
        foreach (var (year, month) in new[]
                 { (2027, 2), (2028, 2), (2027, 4), (2027, 1) })
        {
            var daysInMonth = DateTime.DaysInMonth(year, month);
            for (var transferDay = 2; transferDay <= daysInMonth; transferDay++)
            {
                var sourceOwns = RoomChangeDailyRentQuoteCalculator.Calculate(
                    new DateOnly(year, month, transferDay),
                    4_000_000m, 5_000_000m,
                    RoomChangeTransferDayChargeOwner.SourceRoom,
                    RoomChangeRentRoundingRule.NearestVndAwayFromZero, Rule);
                var targetOwns = RoomChangeDailyRentQuoteCalculator.Calculate(
                    new DateOnly(year, month, transferDay),
                    4_000_000m, 5_000_000m,
                    RoomChangeTransferDayChargeOwner.TargetRoom,
                    RoomChangeRentRoundingRule.NearestVndAwayFromZero, Rule);

                Assert.Equal(transferDay, sourceOwns.SourceChargeableDays);
                Assert.Equal(transferDay - 1, targetOwns.SourceChargeableDays);
            }
        }
    }

    [Fact]
    public void RICC03_Half_Vnd_Rounds_Away_From_Zero_Per_Line()
    {
        // 2027-02 has 28 days; 1_400_014 / 28 = 50_000.5 exactly.
        var transferOn = new DateOnly(2027, 2, 2);
        var quote = RoomChangeDailyRentQuoteCalculator.Calculate(
            transferOn, 1_400_014m, 1_400_014m,
            RoomChangeTransferDayChargeOwner.TargetRoom,
            RoomChangeRentRoundingRule.NearestVndAwayFromZero, Rule);

        Assert.Equal(1, quote.SourceChargeableDays);
        Assert.Equal(50_001m, quote.ProposedSourceRentVnd);

        var truncated = RoomChangeDailyRentQuoteCalculator.Calculate(
            transferOn, 1_400_014m, 1_400_014m,
            RoomChangeTransferDayChargeOwner.TargetRoom,
            RoomChangeRentRoundingRule.TruncateToWholeVnd, Rule);
        Assert.Equal(50_000m, truncated.ProposedSourceRentVnd);
    }

    // ---- plan against real issued invoices ----

    [Fact]
    public void RICC04_Plan_Produces_Source_Credit_And_Target_Debit()
    {
        var month = new YearMonth(2027, 1); // 31 days
        var source = IssuedInvoice(month, 6_000_000m, SourceContract, SourceBuilding);
        var target = IssuedInvoice(month, 4_000_000m, TargetContract, TargetBuilding);
        var quote = Quote(month, day: 11, sourceRent: 6_000_000m,
            targetRent: 4_000_000m);

        var plan = RoomChangeIssuedInvoiceCorrectionPlanner.Plan(
            quote, source, 6_000_000m, 0m, target, 4_000_000m, 0m,
            Reason, Evidence, Guid.NewGuid(), IssuedAt(month).AddHours(6));

        // 31-day month, target owns day 11 => source 10 days, target 21 days.
        Assert.Equal(31, plan.CalendarDaysInMonth);
        Assert.Equal(10, plan.SourceChargeableDays);
        Assert.Equal(21, plan.TargetChargeableDays);

        Assert.Equal(1_935_484m, plan.Source.ProposedRentLineVnd);
        Assert.Equal(-4_064_516m, plan.Source.SignedDeltaVnd);
        Assert.True(plan.Source.IsCredit);
        Assert.Equal(source.Id, plan.Source.IssuedInvoiceId);

        Assert.NotNull(plan.Target);
        Assert.Equal(2_709_677m, plan.Target!.ProposedRentLineVnd);
        Assert.Equal(-1_290_323m, plan.Target.SignedDeltaVnd);
        Assert.True(plan.Target.IsCredit);
        Assert.Equal(target.Id, plan.Target.IssuedInvoiceId);

        Assert.True(plan.HasAnyCorrection);
        Assert.Equal(-5_354_839m, plan.NetDeltaVnd);
        Assert.Equal(Rule, plan.SignedRuleReference);

        // The plan is never an issuance, posting or execution permission.
        Assert.False(plan.CanIssueInvoice);
        Assert.False(plan.CanPostOfficialReceivable);
        Assert.False(plan.CanExecuteRoomChange);

        RoomChangeIssuedInvoiceCorrectionPlanner.VerifyAgainst(
            plan, source, 6_000_000m, 0m, target, 4_000_000m, 0m);
    }

    [Fact]
    public void RICC05_Full_Month_At_Source_Needs_No_Correction()
    {
        var month = new YearMonth(2027, 1); // 31 days
        var source = IssuedInvoice(month, 6_000_000m, SourceContract, SourceBuilding);
        var quote = RoomChangeDailyRentQuoteCalculator.Calculate(
            new DateOnly(month.Year, month.Month, 31), 6_000_000m, 4_000_000m,
            RoomChangeTransferDayChargeOwner.SourceRoom,
            RoomChangeRentRoundingRule.NearestVndAwayFromZero, Rule);

        var plan = RoomChangeIssuedInvoiceCorrectionPlanner.Plan(
            quote, source, 6_000_000m, 0m, null, 0m, 0m,
            Reason, Evidence, Guid.NewGuid(), IssuedAt(month).AddHours(6));

        Assert.Equal(31, plan.SourceChargeableDays);
        Assert.Equal(0, plan.TargetChargeableDays);
        Assert.Null(plan.Target);
        Assert.False(plan.Source.RequiresCorrection);
        Assert.False(plan.HasAnyCorrection);
        Assert.Equal(0m, plan.NetDeltaVnd);
        Assert.Empty(RoomChangeIssuedInvoiceCorrectionPlanner.Materialize(
            plan, source, null, Reason, Evidence,
            Guid.NewGuid(), IssuedAt(month).AddHours(6)));
    }

    [Fact]
    public void RICC06_Target_From_Day_One_Credits_The_Whole_Source_Rent()
    {
        var month = new YearMonth(2028, 2); // leap year, 29 days
        var source = IssuedInvoice(month, 2_900_000m, SourceContract, SourceBuilding);
        var target = IssuedInvoice(month, 3_480_000m, TargetContract, TargetBuilding);
        var quote = Quote(month, day: 1, sourceRent: 2_900_000m,
            targetRent: 3_480_000m);

        var plan = RoomChangeIssuedInvoiceCorrectionPlanner.Plan(
            quote, source, 2_900_000m, 0m, target, 3_480_000m, 0m,
            Reason, Evidence, Guid.NewGuid(), IssuedAt(month).AddHours(6));

        Assert.Equal(29, plan.CalendarDaysInMonth);
        Assert.Equal(0, plan.SourceChargeableDays);
        Assert.Equal(29, plan.TargetChargeableDays);
        Assert.Equal(-2_900_000m, plan.Source.SignedDeltaVnd);
        Assert.Equal(0m, plan.Target!.SignedDeltaVnd);
        Assert.False(plan.Target.RequiresCorrection);
        Assert.Equal(-2_900_000m, plan.NetDeltaVnd);
    }

    [Fact]
    public void RICC07_Materialize_Is_Append_Only_And_Replay_Identical()
    {
        var month = new YearMonth(2027, 4); // 30 days
        var source = IssuedInvoice(month, 6_000_000m, SourceContract, SourceBuilding);
        var target = IssuedInvoice(month, 4_000_000m, TargetContract, TargetBuilding);
        var proposer = Guid.NewGuid();
        var proposedAt = IssuedAt(month).AddHours(6);
        var quote = Quote(month, day: 16, sourceRent: 6_000_000m,
            targetRent: 4_000_000m);

        var first = RoomChangeIssuedInvoiceCorrectionPlanner.Plan(
            quote, source, 6_000_000m, 0m, target, 4_000_000m, 0m,
            Reason, Evidence, proposer, proposedAt);
        var replay = RoomChangeIssuedInvoiceCorrectionPlanner.Plan(
            quote, source, 6_000_000m, 0m, target, 4_000_000m, 0m,
            Reason, Evidence, proposer, proposedAt);

        Assert.Equal(first.Source.AdjustmentId, replay.Source.AdjustmentId);
        Assert.Equal(first.Target!.AdjustmentId, replay.Target!.AdjustmentId);
        Assert.NotEqual(first.Source.AdjustmentId, first.Target.AdjustmentId);

        var proposals = RoomChangeIssuedInvoiceCorrectionPlanner.Materialize(
            first, source, target, Reason, Evidence, proposer, proposedAt);

        Assert.Equal(2, proposals.Length);
        Assert.Equal(source.Id, proposals[0].IssuedInvoiceId);
        Assert.Equal(target.Id, proposals[1].IssuedInvoiceId);
        Assert.Equal(first.Source.SignedDeltaVnd, proposals[0].SignedDeltaAmount);
        Assert.Equal(first.Target.SignedDeltaVnd, proposals[1].SignedDeltaAmount);
        Assert.True(proposals[0].IsCreditNote);
        Assert.True(proposals[1].IsCreditNote);
        Assert.Equal(source.CandidateSha256, proposals[0].InvoiceEvidenceSha256);
        Assert.False(proposals[0].IsOfficialDebtAdjustment);

        // Every materialized proposal is a real, self-verifying audit record.
        foreach (var proposal in proposals) proposal.VerifyAgainst(
            proposal.IssuedInvoiceId == source.Id ? source : target);
    }

    [Fact]
    public void RICC08_Source_Shortfall_Below_Captured_Receipts_Is_Refused()
    {
        var month = new YearMonth(2027, 1);
        var source = IssuedInvoice(month, 6_000_000m, SourceContract, SourceBuilding);
        var quote = Quote(month, day: 11, sourceRent: 6_000_000m,
            targetRent: 4_000_000m);

        // Source keeps 1_935_484; anything captured above that needs a
        // separate reviewed refund, never a silent credit.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RoomChangeIssuedInvoiceCorrectionPlanner.Plan(
                quote, source, 6_000_000m, 2_000_000m, null, 0m, 0m,
                Reason, Evidence, Guid.NewGuid(), IssuedAt(month).AddHours(6)));
        Assert.Contains("below money already received", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RICC09_Tampered_Or_Unapproved_Quote_Is_Refused()
    {
        var month = new YearMonth(2027, 1);
        var source = IssuedInvoice(month, 6_000_000m, SourceContract, SourceBuilding);
        var quote = Quote(month, day: 11, sourceRent: 6_000_000m,
            targetRent: 4_000_000m);

        // A hand-built record whose arithmetic no longer matches its inputs.
        var forged = quote with { ProposedSourceRentVnd = 5_999_999m };
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeIssuedInvoiceCorrectionPlanner.Plan(
                forged, source, 6_000_000m, 0m, null, 0m, 0m,
                Reason, Evidence, Guid.NewGuid(), IssuedAt(month).AddHours(6)));

        // Missing signature / proposal identity fails closed as well.
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeIssuedInvoiceCorrectionPlanner.Plan(
                null, source, 6_000_000m, 0m, null, 0m, 0m,
                Reason, Evidence, Guid.NewGuid(), IssuedAt(month).AddHours(6)));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeIssuedInvoiceCorrectionPlanner.Plan(
                quote, source, 6_000_000m, 0m, null, 0m, 0m,
                "short", Evidence, Guid.NewGuid(), IssuedAt(month).AddHours(6)));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeIssuedInvoiceCorrectionPlanner.Plan(
                quote, source, 6_000_000m, 0m, null, 0m, 0m,
                Reason, Evidence, Guid.Empty, IssuedAt(month).AddHours(6)));
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeIssuedInvoiceCorrectionPlanner.Plan(
                quote, source, 6_000_000m, 0m, null, 0m, 0m,
                Reason, Evidence, Guid.NewGuid(),
                DateTime.SpecifyKind(IssuedAt(month), DateTimeKind.Unspecified)));
    }

    [Fact]
    public void RICC10_Cross_Month_And_Missing_Target_Invoice_Are_Refused()
    {
        var month = new YearMonth(2027, 1);
        var otherMonth = IssuedInvoice(new YearMonth(2027, 2),
            6_000_000m, SourceContract, SourceBuilding);
        var quote = Quote(month, day: 11, sourceRent: 6_000_000m,
            targetRent: 4_000_000m);

        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeIssuedInvoiceCorrectionPlanner.Plan(
                quote, otherMonth, 6_000_000m, 0m, null, 0m, 0m,
                Reason, Evidence, Guid.NewGuid(), IssuedAt(month).AddHours(6)));

        var source = IssuedInvoice(month, 6_000_000m, SourceContract, SourceBuilding);
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeIssuedInvoiceCorrectionPlanner.Plan(
                quote, source, 6_000_000m, 0m, null, 0m, 0m,
                Reason, Evidence, Guid.NewGuid(), IssuedAt(month).AddHours(6)));

        // One invoice cannot be both the source and the target of a transfer.
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeIssuedInvoiceCorrectionPlanner.Plan(
                quote, source, 6_000_000m, 0m, source, 6_000_000m, 0m,
                Reason, Evidence, Guid.NewGuid(), IssuedAt(month).AddHours(6)));
    }

    [Fact]
    public void RICC11_Non_Whole_Vnd_And_Stale_Evidence_Are_Refused()
    {
        var month = new YearMonth(2027, 1);
        var source = IssuedInvoice(month, 6_000_000m, SourceContract, SourceBuilding);
        var quote = Quote(month, day: 11, sourceRent: 6_000_000m,
            targetRent: 4_000_000m);

        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeIssuedInvoiceCorrectionPlanner.Plan(
                quote, source, 6_000_000.5m, 0m, null, 0m, 0m,
                Reason, Evidence, Guid.NewGuid(), IssuedAt(month).AddHours(6)));

        // Whole-month-at-source variant so no target invoice is involved.
        var fullMonth = RoomChangeDailyRentQuoteCalculator.Calculate(
            new DateOnly(month.Year, month.Month, 31), 6_000_000m, 4_000_000m,
            RoomChangeTransferDayChargeOwner.SourceRoom,
            RoomChangeRentRoundingRule.NearestVndAwayFromZero, Rule);
        var plan = RoomChangeIssuedInvoiceCorrectionPlanner.Plan(
            fullMonth, source, 6_000_000m, 0m, null, 0m, 0m,
            Reason, Evidence, Guid.NewGuid(), IssuedAt(month).AddHours(6));

        // A receipt captured after the plan was drawn invalidates it.
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeIssuedInvoiceCorrectionPlanner.VerifyAgainst(
                plan, source, 6_000_000m, 1_000m, null, 0m, 0m));
        // So does a reissued rent line.
        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeIssuedInvoiceCorrectionPlanner.VerifyAgainst(
                plan, source, 6_100_000m, 0m, null, 0m, 0m));
    }

    [Fact]
    public void RICC12_Proposal_Timestamp_Cannot_Precede_Issuance()
    {
        var month = new YearMonth(2027, 1);
        var source = IssuedInvoice(month, 6_000_000m, SourceContract, SourceBuilding);
        var quote = Quote(month, day: 11, sourceRent: 6_000_000m,
            targetRent: 4_000_000m);

        Assert.Throws<InvalidOperationException>(() =>
            RoomChangeIssuedInvoiceCorrectionPlanner.Plan(
                quote, source, 6_000_000m, 0m, null, 0m, 0m,
                Reason, Evidence, Guid.NewGuid(),
                IssuedAt(month).AddHours(-1)));
    }

    // ---- oracle: exact rational proration, rounded half away from zero ----

    private static decimal OracleProrate(
        decimal monthlyRent, int chargeableDays, int daysInMonth)
    {
        if (chargeableDays == 0) return 0m;
        var numerator = new BigInteger(decimal.Truncate(monthlyRent)) * chargeableDays;
        var denominator = new BigInteger(daysInMonth);
        // floor((2n + d) / 2d) == round-half-up of n/d for a positive n.
        return (decimal)((2 * numerator + denominator) / (2 * denominator));
    }

    // ---- fixtures ----

    private readonly record struct YearMonth(int Year, int Month);

    private static RoomChangeDailyRentQuote Quote(
        YearMonth month, int day, decimal sourceRent, decimal targetRent)
        => RoomChangeDailyRentQuoteCalculator.Calculate(
            new DateOnly(month.Year, month.Month, day),
            sourceRent, targetRent,
            RoomChangeTransferDayChargeOwner.TargetRoom,
            RoomChangeRentRoundingRule.NearestVndAwayFromZero, Rule);

    private static DateTime IssuedAt(YearMonth month)
        => new(month.Year, month.Month, 2, 9, 0, 0, DateTimeKind.Utc);

    private static MonthlyIssuedInvoiceRecord IssuedInvoice(
        YearMonth month, decimal rentAmount, Guid contractId, Guid buildingId)
    {
        var preparedAt = new DateTime(
            month.Year, month.Month, 1, 8, 0, 0, DateTimeKind.Utc);
        var dueDate = new DateOnly(month.Year, month.Month,
            DateTime.DaysInMonth(month.Year, month.Month));
        var archive = MonthlyInvoiceCandidateLedgerRecord.Capture(
            Guid.NewGuid(),
            new MonthlyInvoiceCandidateSnapshot(
                Guid.NewGuid(), contractId, Guid.NewGuid(), buildingId,
                month.Year, month.Month, Guid.NewGuid(), preparedAt,
                dueDate, "APPROVED-MONTHLY-DUE-001",
                rentAmount, 35_000m, 15_000m,
                AuxiliaryFeeAssessmentDecision.ExplicitlyNoCharges,
                "EXPLICIT-NO-AUXILIARY-SERVICES",
                Array.Empty<FinalInvoiceAuxiliaryChargeProposal>(),
                0m, rentAmount + 50_000m,
                new[]
                {
                    Line(UtilityType.Electricity, 10m, 3_500m, 100m),
                    Line(UtilityType.Water, 1m, 15_000m, 40m)
                }));
        var review = MonthlyInvoiceIndependentReviewRecord.Capture(
            Guid.NewGuid(), archive, Guid.NewGuid(),
            preparedAt.AddHours(1), true,
            "Reviewed original rates and physical meter readings");
        return MonthlyIssuedInvoiceRecord.Issue(
            Guid.NewGuid(), "NTM-" + Guid.NewGuid().ToString("N")[..12],
            archive.ReviewedByUserId, preparedAt.AddHours(2),
            archive, review);
    }

    private static MonthlyInvoiceCandidateUtilityLine Line(
        UtilityType kind, decimal quantity, decimal price, decimal previous)
        => new(kind, Guid.NewGuid(), quantity, price, quantity * price,
            new[]
            {
                new MonthlyInvoiceCandidateMeterSource(
                    Guid.NewGuid(), Guid.NewGuid(),
                    MeterUsageSourceType.MonthlyReading,
                    new DateTime(2026, 10, 31), previous, previous + quantity)
            });
}
