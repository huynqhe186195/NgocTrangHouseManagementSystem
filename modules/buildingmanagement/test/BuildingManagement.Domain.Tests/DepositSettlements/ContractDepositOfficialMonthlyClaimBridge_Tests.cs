using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Invoices;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.DomainTests.DepositSettlements;

public sealed class ContractDepositOfficialMonthlyClaimBridge_Tests
{
    private static readonly DateTime Now =
        new(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly AsOf = new(2026, 10, 31);

    private static (MonthlyInvoiceCandidateLedgerRecord Source,
        MonthlyInvoiceIndependentReviewRecord Review,
        MonthlyIssuedInvoiceRecord Issued,
        OfficialInvoiceReceivablePostingRecord Posting) Invoice()
    {
        var type = Enum.GetValues<MeterUsageSourceType>().First();
        MonthlyInvoiceCandidateUtilityLine Utility(UtilityType utility,
            decimal quantity, decimal price, decimal previous, decimal current)
            => new(utility, Guid.NewGuid(), quantity, price, quantity * price,
                new[] { new MonthlyInvoiceCandidateMeterSource(
                    Guid.NewGuid(), Guid.NewGuid(), type,
                    new DateTime(2026, 10, 30), previous, current) });

        var e = Utility(UtilityType.Electricity, 10m, 3_500m, 100m, 110m);
        var w = Utility(UtilityType.Water, 1m, 15_000m, 10m, 11m);
        var candidate = new MonthlyInvoiceCandidateSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            2026, 10, Guid.NewGuid(), Now,
            new DateOnly(2026, 11, 5), "MONTHLY-DUE-DECISION-001",
            3_000_000m, 35_000m, 15_000m,
            AuxiliaryFeeAssessmentDecision.ExplicitlyNoCharges,
            "NO-AUXILIARY-FEES-APPROVED-001",
            Array.Empty<FinalInvoiceAuxiliaryChargeProposal>(),
            0m, 3_050_000m, new[] { e, w });
        var frozen = MonthlyInvoiceCandidateLedgerRecord.Capture(
            Guid.NewGuid(), candidate);
        var review = MonthlyInvoiceIndependentReviewRecord.Capture(
            Guid.NewGuid(), frozen, Guid.NewGuid(), Now.AddHours(1),
            true, "Independent monthly review and source validation");
        var issue = MonthlyIssuedInvoiceRecord.Issue(
            Guid.NewGuid(), "NTM-BRIDGE-001", frozen.ReviewedByUserId,
            Now.AddHours(2), frozen, review);
        var post = OfficialInvoiceReceivablePostingRecord.PostMonthly(
            Guid.NewGuid(), issue, frozen, review, Guid.NewGuid(),
            Now.AddHours(3));
        return (frozen, review, issue, post);
    }

    private static ContractDepositOfficialMonthlyClaims Assess(
        (MonthlyInvoiceCandidateLedgerRecord Source,
         MonthlyInvoiceIndependentReviewRecord Review,
         MonthlyIssuedInvoiceRecord Issued,
         OfficialInvoiceReceivablePostingRecord Posting) f,
        IReadOnlyCollection<OfficialInvoiceReceiptEvidence>? receipts = null,
        IReadOnlyCollection<OfficialInvoiceReceiptReviewRecord>? reviews = null,
        IReadOnlyCollection<MonthlyInvoiceAdjustmentRecord>? adjustments = null,
        IReadOnlyCollection<MonthlyInvoiceAdjustmentDecisionRecord>? decisions = null)
        => ContractDepositOfficialMonthlyClaimBridge.Assess(
            f.Posting, f.Issued, f.Source, f.Review, AsOf,
            receipts ?? Array.Empty<OfficialInvoiceReceiptEvidence>(),
            reviews ?? Array.Empty<OfficialInvoiceReceiptReviewRecord>(),
            adjustments ?? Array.Empty<MonthlyInvoiceAdjustmentRecord>(),
            decisions ?? Array.Empty<MonthlyInvoiceAdjustmentDecisionRecord>());

    [Fact]
    public void DEP_BRIDGE01_Frozen_Official_Unpaid_Invoice_Yields_Only_Actual_Charge_Categories()
    {
        var f = Invoice();
        var result = Assess(f);
        Assert.True(result.CanPreview);
        Assert.Equal(3, result.Claims.Count);
        Assert.Equal(3_000_000m, result.Claims.Single(x =>
            x.Category == ContractDepositOffsetCategory.UnpaidRent).OutstandingVnd);
        Assert.Equal(35_000m, result.Claims.Single(x =>
            x.Category == ContractDepositOffsetCategory.Electricity).OutstandingVnd);
        Assert.Equal(15_000m, result.Claims.Single(x =>
            x.Category == ContractDepositOffsetCategory.Water).OutstandingVnd);
        Assert.Equal(f.Posting.OriginalAmount,
            result.Claims.Sum(x => x.OutstandingVnd));
        Assert.False(result.CanPostOffset);
        Assert.False(result.CanRefund);
        var preview = ContractDepositOffsetPreviewPolicy.Assess(
            3_000_000m, f.Posting.ContractId, Guid.NewGuid(), true,
            "SIGNED-OWNER-OI04-001", result.Claims);
        Assert.True(preview.IsEvidenceCompleteForPreview);
        Assert.Equal(0m, preview.ProposedRefundableVnd);
        Assert.Equal(50_000m, preview.ProposedUncoveredDebtVnd);
        Assert.False(preview.CanPostDebtOffset);
    }

    [Fact]
    public void DEP_BRIDGE02_Claim_Ids_Are_Stable_For_The_Same_Posted_Invoice()
    {
        var f = Invoice();
        Assert.Equal(Assess(f).Claims.Select(x => x.ClaimId),
            Assess(f).Claims.Select(x => x.ClaimId));
        Assert.Equal(3, Assess(f).Claims.Select(x => x.ClaimId).Distinct().Count());
    }

    [Fact]
    public void DEP_BRIDGE03_Receipt_Without_Reviewed_Category_Allocation_Blocks()
    {
        var f = Invoice();
        var receipt = new OfficialInvoiceReceiptEvidence(
            Guid.NewGuid(), Guid.NewGuid(), f.Issued.Id, f.Posting.ContractId,
            100_000m, Now.AddHours(4), Guid.NewGuid(), Now.AddHours(5),
            "BANK-MANUAL-001", "BANK-PROOF: SIGNED-SOURCE-001");
        var result = Assess(f, new[] { receipt });
        Assert.False(result.CanPreview);
        Assert.Contains("PartialOrUnresolvedPaymentsNeedLineAllocation", result.Blockers);
        Assert.Empty(result.Claims);
    }

    [Fact]
    public void DEP_BRIDGE04_Any_Proposed_Adjustment_Blocks_Per_Line_Offset_Claim()
    {
        var f = Invoice();
        var change = MonthlyInvoiceAdjustmentRecord.Propose(
            Guid.NewGuid(), f.Issued, Guid.NewGuid(), Now.AddHours(4),
            -10_000m, "Correcting a documented monthly charge",
            "SIGNED-CREDIT-EVIDENCE-001");
        var result = Assess(f, adjustments: new[] { change });
        Assert.False(result.CanPreview);
        Assert.Contains("AdjustedInvoiceNeedsLineAllocation", result.Blockers);
        Assert.Empty(result.Claims);
    }

    [Fact]
    public void DEP_BRIDGE05_Wrong_Persisted_Posting_And_Issued_Source_Is_Rejected()
    {
        var f = Invoice();
        var unrelated = Invoice();
        Assert.Throws<InvalidDataException>(() =>
            ContractDepositOfficialMonthlyClaimBridge.Assess(
                unrelated.Posting, f.Issued, f.Source, f.Review, AsOf,
                Array.Empty<OfficialInvoiceReceiptEvidence>(),
                Array.Empty<OfficialInvoiceReceiptReviewRecord>(),
                Array.Empty<MonthlyInvoiceAdjustmentRecord>(),
                Array.Empty<MonthlyInvoiceAdjustmentDecisionRecord>()));
    }

    [Fact]
    public void DEP_BRIDGE06_Missing_Official_Evidence_History_Denies()
    {
        var f = Invoice();
        var result = ContractDepositOfficialMonthlyClaimBridge.Assess(
            f.Posting, f.Issued, f.Source, f.Review, AsOf,
            null, Array.Empty<OfficialInvoiceReceiptReviewRecord>(),
            Array.Empty<MonthlyInvoiceAdjustmentRecord>(),
            Array.Empty<MonthlyInvoiceAdjustmentDecisionRecord>());
        Assert.Contains("OfficialMonthlyHistoryNotLoaded", result.Blockers);
        Assert.Empty(result.Claims);
    }

    [Fact]
    public void DEP_BRIDGE07_No_Client_Decision_Can_Post_Real_Debt_Offset()
    {
        var f = Invoice();
        var preview = Assess(f);
        Assert.All(preview.Claims, x =>
        {
            Assert.True(x.IndependentlyVerified);
            Assert.False(x.IsDisputed);
            Assert.NotEqual(Guid.Empty, x.ClaimId);
        });
        Assert.False(preview.CanPostOffset);
        Assert.False(preview.CanRefund);
        Assert.False(ContractDepositOffsetPreviewPolicy.Assess(
            3_000_000m, f.Posting.ContractId, Guid.NewGuid(), false,
            "SIGNED-OWNER-OI04-001", preview.Claims).IsEvidenceCompleteForPreview);
    }
    [Fact]
    public void DEP_BRIDGE08_Future_Posting_Cannot_Be_Treated_As_Past_Debt()
    {
        var f = Invoice();
        var result = ContractDepositOfficialMonthlyClaimBridge.Assess(
            f.Posting, f.Issued, f.Source, f.Review,
            new DateOnly(2026, 10, 29),
            Array.Empty<OfficialInvoiceReceiptEvidence>(),
            Array.Empty<OfficialInvoiceReceiptReviewRecord>(),
            Array.Empty<MonthlyInvoiceAdjustmentRecord>(),
            Array.Empty<MonthlyInvoiceAdjustmentDecisionRecord>());
        Assert.Contains("OfficialPostingIsAfterEvaluationDate", result.Blockers);
        Assert.Empty(result.Claims);
    }

}
