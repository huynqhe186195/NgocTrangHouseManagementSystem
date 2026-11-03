using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Contracts;
using BuildingManagement.Rooms;
using BuildingManagement.Invoices;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.RoomChanges;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.DomainTests.RoomChanges;

public sealed class RoomChangeOfficialSourceRentEvidenceBridge_Tests
{
    private static readonly DateTime Now =
        new(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly AsOf = new(2026, 10, 31);

    private sealed record F(
        MonthlyInvoiceCandidateLedgerRecord Frozen,
        MonthlyInvoiceIndependentReviewRecord InvoiceReview,
        MonthlyIssuedInvoiceRecord Issued,
        OfficialInvoiceReceivablePostingRecord Posting,
        OfficialInvoiceReceiptEvidence Receipt,
        OfficialInvoiceReceiptReviewRecord Bank,
        RoomChangeDailyRentQuote Quote);

    private static F Fixture(decimal paymentVnd = 250_000m)
    {
        var type = Enum.GetValues<MeterUsageSourceType>().First();
        MonthlyInvoiceCandidateUtilityLine Utility(
            UtilityType kind, decimal amount, decimal unit,
            decimal previous, decimal current) =>
            new(kind, Guid.NewGuid(), amount, unit, amount * unit,
                new[] { new MonthlyInvoiceCandidateMeterSource(
                    Guid.NewGuid(), Guid.NewGuid(), type,
                    new DateTime(2026, 10, 30), previous, current) });

        var snapshot = new MonthlyInvoiceCandidateSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            2026, 10, Guid.NewGuid(), Now,
            new DateOnly(2026, 11, 5), "SIGNED-MONTHLY-DUE-2026",
            3_000_000m, 35_000m, 15_000m,
            AuxiliaryFeeAssessmentDecision.ExplicitlyNoCharges,
            "SIGNED-NO-ADDITIONAL-FEES-OCT2026",
            Array.Empty<FinalInvoiceAuxiliaryChargeProposal>(), 0m,
            3_050_000m, new[]
            {
                Utility(UtilityType.Electricity, 10m, 3500m, 100m, 110m),
                Utility(UtilityType.Water, 1m, 15000m, 10m, 11m)
            });
        var frozen = MonthlyInvoiceCandidateLedgerRecord.Capture(
            Guid.NewGuid(), snapshot);
        var invoiceReview = MonthlyInvoiceIndependentReviewRecord.Capture(
            Guid.NewGuid(), frozen, Guid.NewGuid(), Now.AddHours(1), true,
            "Independent actual monthly source invoice review");
        var issued = MonthlyIssuedInvoiceRecord.Issue(
            Guid.NewGuid(), "NT-MONTH-TRANSFER-D03-001",
            frozen.ReviewedByUserId,
            Now.AddHours(2), frozen, invoiceReview);
        var posting = OfficialInvoiceReceivablePostingRecord.PostMonthly(
            Guid.NewGuid(), issued, frozen, invoiceReview,
            Guid.NewGuid(), Now.AddHours(3));
        var payment = new Payment(
            Guid.NewGuid(), "BANK-D03-RECEIPT-001",
            Guid.NewGuid(), PaymentPurpose.InvoicePayment,
            PaymentMethod.BankTransfer, PaymentChannel.Manual,
            paymentVnd, contractId: issued.ContractId,
            invoiceId: issued.Id, transactionReference: "BANK-D03-TRANSACTION-001",
            notes: "BANK-PROOF: SIGNED-STATEMENT-RECEIPT-D03-2026");
        payment.Complete(Now.AddHours(4), "BANK-D03-TRANSACTION-001");
        var allocated = MonthlyInvoicePaymentAllocationRecord.Capture(
            Guid.NewGuid(), issued, payment, Guid.NewGuid(), Now.AddHours(5));
        var receipt = OfficialInvoiceReceiptEvidence.FromMonthly(
            allocated, payment);
        var bank = OfficialInvoiceReceiptReviewRecord.Review(
            Guid.NewGuid(), posting, receipt, Guid.NewGuid(),
            Now.AddHours(6), true, "SIGNED-BANK-STATEMENT-D03-001",
            "Different human confirmed original bank receipt evidence");
        var quote = RoomChangeDailyRentQuoteCalculator.Calculate(
            new DateOnly(2026, 10, 15), 3_000_000m, 4_000_000m,
            RoomChangeTransferDayChargeOwner.TargetRoom,
            RoomChangeRentRoundingRule.NearestVndAwayFromZero,
            "SIGNED-TRANSFER-PRICING-REVIEW-OCT2026");
        return new F(frozen, invoiceReview, issued, posting, receipt, bank, quote);
    }

    private static OfficialMonthlyReceiptLineAllocationRecord Line(
        F f, ContractDepositOffsetCategory category, decimal vnd)
        => OfficialMonthlyReceiptLineAllocationRecord.Capture(
            Guid.NewGuid(), f.Posting, f.Receipt, f.Bank,
            category, vnd, Guid.NewGuid(), Now.AddHours(7),
            "SIGNED-REVIEWED-RECEIPT-LINE-ALLOCATION-D03");

    private static RoomChangeOfficialSourceRentEvidenceResult Assess(
        F f,
        IReadOnlyCollection<OfficialInvoiceReceiptEvidence>? receipts = null,
        IReadOnlyCollection<OfficialInvoiceReceiptReviewRecord>? reviews = null,
        IReadOnlyCollection<OfficialMonthlyReceiptLineAllocationRecord>? lines = null,
        IReadOnlyCollection<MonthlyInvoiceAdjustmentRecord>? proposals = null,
        IReadOnlyCollection<MonthlyInvoiceAdjustmentDecisionRecord>? decisions = null,
        RoomChangeDailyRentQuote? quote = null,
        DateOnly? asOf = null,
        OfficialInvoiceReceivablePostingRecord? posting = null)
        => RoomChangeOfficialSourceRentEvidenceBridge.Assess(
            quote ?? f.Quote, posting ?? f.Posting,
            f.Issued, f.Frozen, f.InvoiceReview, asOf ?? AsOf,
            receipts ?? Array.Empty<OfficialInvoiceReceiptEvidence>(),
            reviews ?? Array.Empty<OfficialInvoiceReceiptReviewRecord>(),
            lines ?? Array.Empty<OfficialMonthlyReceiptLineAllocationRecord>(),
            proposals ?? Array.Empty<MonthlyInvoiceAdjustmentRecord>(),
            decisions ?? Array.Empty<MonthlyInvoiceAdjustmentDecisionRecord>());

    private static OfficialMonthlyReceiptLineAllocationRecord[] FullSplit(F f)
        => new[]
        {
            Line(f, ContractDepositOffsetCategory.UnpaidRent, 200_000m),
            Line(f, ContractDepositOffsetCategory.Electricity, 35_000m),
            Line(f, ContractDepositOffsetCategory.Water, 15_000m)
        };

    private static (
        Contract Source, RoomChangeRequest Request,
        RoomChangeTenantRosterSnapshotRecord Roster,
        RoomChangeBusinessPolicyRecord Policy,
        RoomChangeBusinessPolicyReviewRecord PolicyReview,
        RoomChangeDepositDifferenceFacts DepositFacts,
        Guid TargetId) Transfer(F f)
    {
        var begin = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var source = new Contract(
            f.Frozen.ContractId, "SOURCE-D03-TRANSFER-OCT2026",
            f.Frozen.RoomId, new DateTime(2026, 9, 1),
            null, 3_000_000m);
        source.Activate();
        var request = RoomChangeRequest.Submit(
            Guid.NewGuid(), source.Id, source.RoomId, Guid.NewGuid(),
            f.Frozen.BuildingId, f.Quote.TransferDate,
            "Documented day-proration of official October rent",
            Guid.NewGuid(), begin, ContractStatus.Active,
            RoomStatus.Occupied, RoomStatus.Available);
        var roster = RoomChangeTenantRosterSnapshotRecord.Capture(
            Guid.NewGuid(), request, new[] {
                new RoomChangeRosterMember(
                    Guid.NewGuid(), ContractTenantRole.PrimaryTenant)
            }, Guid.NewGuid(), begin.AddSeconds(1));
        const string depositPolicy = "SIGNED-OWNER-DEPOSIT-REQUIREMENT-OCT2026";
        var policy = RoomChangeBusinessPolicyRecord.Propose(
            Guid.NewGuid(), request, roster,
            RoomChangeContractTreatment.NewContract,
            RoomChangePricingTreatment.AgreedDayProration,
            RoomChangeDepositTreatment.CarryWithVerifiedTopUp,
            RoomChangeTenantTreatment.AllCurrentTenants,
            "SIGNED-LINKED-NEW-CONTRACT-D03-OCT2026",
            f.Quote.SignedRuleReference,
            depositPolicy,
            "SIGNED-TRANSFER-ALL-PARTIES-D03-OCT2026",
            Guid.NewGuid(), begin.AddSeconds(2));
        var review = RoomChangeBusinessPolicyReviewRecord.Review(
            Guid.NewGuid(), policy, request, roster, Guid.NewGuid(),
            begin.AddSeconds(3), true,
            "INDEPENDENT-REVIEW-D03-OCTOBER-2026",
            "Reviewed complete source and target policy documentation");
        var targetId = Guid.NewGuid();
        var deposit = new RoomChangeDepositDifferenceFacts(
            source.Id, targetId, 3_000_000m, 0m, 4_000_000m,
            "CASH-BACKED-SOURCE-RECEIPT-PROOF-D03",
            depositPolicy, null, false, false);
        return (source, request, roster, policy, review, deposit, targetId);
    }

    private static RoomChangeMidMonthFinancialReconciliationPacket
        PreparedFromOfficial(F f,
        MonthlyInvoiceAdjustmentRecord[]? proposals = null,
        MonthlyInvoiceAdjustmentDecisionRecord[]? decisions = null,
        Contract? overrideSource = null)
    {
        var t = Transfer(f);
        return RoomChangeMidMonthFinancialReconciliationPacketFactory
            .PrepareFromOfficialMonthlyEvidence(
                Guid.NewGuid(), t.Request, overrideSource ?? t.Source,
                t.TargetId, t.Roster, t.Policy, t.PolicyReview,
                f.Quote, t.DepositFacts, f.Posting, f.Issued,
                f.Frozen, f.InvoiceReview, AsOf,
                new[] { f.Receipt }, new[] { f.Bank },
                FullSplit(f),
                proposals ?? Array.Empty<MonthlyInvoiceAdjustmentRecord>(),
                decisions ?? Array.Empty<MonthlyInvoiceAdjustmentDecisionRecord>(),
                Guid.NewGuid(), new DateTime(
                    2026, 11, 1, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void TRF_D03_15_Official_Receipt_Source_Reaches_One_Shared_Transfer_Packet()
    {
        var f = Fixture();
        var packet = PreparedFromOfficial(f);
        packet.VerifyFingerprint();
        Assert.Equal(f.Frozen.ContractId, packet.SourceContractId);
        Assert.Equal(f.Issued.Id,
            packet.SourceRentLedger.IssuedMonthlyInvoiceId);
        Assert.Equal(200_000m,
            packet.SourceRentLedger.ReviewedRentReceiptsVnd);
        Assert.Equal(1_154_839m,
            packet.Rent.RemainingProposedSourceRentVnd);
        Assert.Equal(1_000_000m,
            packet.Deposit.UnpaidTopUpRequirementVnd);
        Assert.False(packet.CanExecuteRoomChange);
        Assert.False(packet.IsOfficialInvoicePosting);
        Assert.False(packet.IsIndependentlyCashVerified);
    }

    [Fact]
    public void TRF_D03_16_Unclassified_Invoice_Credit_Blocks_Shared_Packet()
    {
        var f = Fixture();
        var correction = MonthlyInvoiceAdjustmentRecord.Propose(
            Guid.NewGuid(), f.Issued, Guid.NewGuid(),
            Now.AddHours(8), -15_000m,
            "Source invoice service credit not classified as rent",
            "SIGNED-UNCLASSIFIED-SERVICE-CREDIT-D03");
        Assert.Throws<InvalidOperationException>(() =>
            PreparedFromOfficial(f, proposals: new[] { correction }));
    }

    [Fact]
    public void TRF_D03_17_Foreign_Contract_Cannot_Consume_Posted_Monthly_Rent()
    {
        var f = Fixture();
        var foreign = new Contract(Guid.NewGuid(), "FOREIGN-CONTRACT-D03",
            f.Frozen.RoomId, new DateTime(2026, 9, 1), null, 3_000_000m);
        foreign.Activate();
        Assert.Throws<InvalidOperationException>(() =>
            PreparedFromOfficial(f, overrideSource: foreign));
    }

    [Fact]
    public void TRF_D03_01_Unpaid_Issued_Monthly_Rent_Only_Not_Whole_Invoice()
    {
        var f = Fixture();
        var result = Assess(f);
        Assert.True(result.CanPreview, string.Join(",", result.Blockers));
        Assert.Equal(f.Issued.Id, result.Snapshot!.IssuedMonthlyInvoiceId);
        Assert.Equal(3_000_000m, result.Snapshot.OriginalIssuedRentLineVnd);
        Assert.Equal(0m, result.Snapshot.ReviewedRentReceiptsVnd);
        Assert.Null(result.Snapshot.ReceiptAllocationEvidenceReference);
        Assert.Equal(f.Frozen.FrozenEvidenceSha256,
            result.Snapshot.FrozenInvoiceEvidenceSha256);
        Assert.Equal(f.Posting.Id, result.OfficialReceivablePostingId);
        Assert.Equal(1_354_839m, result.Preview!.ProposedSourceRentVnd);
        Assert.False(result.CanPostInvoiceAdjustment);
        Assert.False(result.CanExecuteRoomChange);
        Assert.False(result.CanRefundOrCollectCash);
    }

    [Fact]
    public void TRF_D03_02_Reviewed_MultiCategory_Receipt_Uses_Only_Rent_Lines()
    {
        var f = Fixture();
        var result = Assess(f,
            receipts: new[] { f.Receipt }, reviews: new[] { f.Bank },
            lines: FullSplit(f));
        Assert.True(result.CanPreview, string.Join(",", result.Blockers));
        Assert.Equal(200_000m, result.Snapshot!.ReviewedRentReceiptsVnd);
        Assert.Equal(1_154_839m, result.Preview!.RemainingProposedSourceRentVnd);
        Assert.Equal(1, result.ReviewedReceiptCount);
        Assert.Equal(1, result.ReviewedRentLineCount);
        Assert.StartsWith("REVIEWED-RENT-LINE-ALLOCATIONS-SHA256:",
            result.Snapshot.ReceiptAllocationEvidenceReference);
        Assert.False(result.IsBankGatewayVerified);
    }

    [Fact]
    public void TRF_D03_03_Utility_Only_Receipt_Does_Not_Reduce_Rent()
    {
        var f = Fixture(50_000m);
        var result = Assess(f,
            receipts: new[] { f.Receipt }, reviews: new[] { f.Bank },
            lines: new[] {
                Line(f, ContractDepositOffsetCategory.Electricity, 35_000m),
                Line(f, ContractDepositOffsetCategory.Water, 15_000m)
            });
        Assert.True(result.CanPreview, string.Join(",", result.Blockers));
        Assert.Equal(0m, result.Snapshot!.ReviewedRentReceiptsVnd);
        Assert.Equal(1_354_839m,
            result.Preview!.RemainingProposedSourceRentVnd);
        Assert.Null(result.Snapshot.ReceiptAllocationEvidenceReference);
    }

    [Fact]
    public void TRF_D03_04_Incomplete_Category_Split_Blocks_All_Figures()
    {
        var f = Fixture();
        var result = Assess(f,
            receipts: new[] { f.Receipt }, reviews: new[] { f.Bank },
            lines: new[] {
                Line(f, ContractDepositOffsetCategory.UnpaidRent, 200_000m)
            });
        Assert.False(result.CanPreview);
        Assert.Null(result.Snapshot);
        Assert.Null(result.Preview);
    }

    [Fact]
    public void TRF_D03_05_Duplicate_Payment_Or_Category_Blocks()
    {
        var f = Fixture();
        var duplicate = Assess(f,
            receipts: new[] { f.Receipt, f.Receipt },
            reviews: new[] { f.Bank }, lines: FullSplit(f));
        Assert.False(duplicate.CanPreview);
        var a = Line(f, ContractDepositOffsetCategory.UnpaidRent, 125_000m);
        var b = Line(f, ContractDepositOffsetCategory.UnpaidRent, 125_000m);
        var sameCategory = Assess(f,
            receipts: new[] { f.Receipt }, reviews: new[] { f.Bank },
            lines: new[] { a, b });
        Assert.False(sameCategory.CanPreview);
    }

    [Fact]
    public void TRF_D03_06_Unreviewed_Or_Rejected_Receipt_Never_Credits_Rent()
    {
        var f = Fixture();
        var unsigned = Assess(f,
            receipts: new[] { f.Receipt }, reviews: Array.Empty<OfficialInvoiceReceiptReviewRecord>(),
            lines: FullSplit(f));
        Assert.False(unsigned.CanPreview);
        var rejected = OfficialInvoiceReceiptReviewRecord.Review(
            Guid.NewGuid(), f.Posting, f.Receipt, Guid.NewGuid(),
            Now.AddHours(6), false, "",
            "Bank statement does not support this reported payment");
        var refused = Assess(f,
            receipts: new[] { f.Receipt }, reviews: new[] { rejected },
            lines: FullSplit(f));
        Assert.False(refused.CanPreview);
    }

    [Fact]
    public void TRF_D03_07_Mismatched_Posting_And_Issued_Invoice_Denied()
    {
        var a = Fixture();
        var b = Fixture();
        var result = Assess(a, posting: b.Posting);
        Assert.False(result.CanPreview);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void TRF_D03_08_Tariff_Or_Billing_Month_Mismatch_Blocks()
    {
        var f = Fixture();
        var changedRent = f.Quote with { SourceFullMonthlyRentVnd = 2_000_000m };
        Assert.False(Assess(f, quote: changedRent).CanPreview);
        var nov = RoomChangeDailyRentQuoteCalculator.Calculate(
            new DateOnly(2026, 11, 15), 3_000_000m, 4_000_000m,
            RoomChangeTransferDayChargeOwner.TargetRoom,
            RoomChangeRentRoundingRule.NearestVndAwayFromZero,
            f.Quote.SignedRuleReference);
        Assert.False(Assess(f, quote: nov).CanPreview);
    }

    [Fact]
    public void TRF_D03_09_Pending_Adjustment_To_Full_Invoice_Not_Assigned_To_Rent()
    {
        var f = Fixture();
        var correction = MonthlyInvoiceAdjustmentRecord.Propose(
            Guid.NewGuid(), f.Issued, Guid.NewGuid(), Now.AddHours(8),
            -10_000m, "Correction of an unclassified issued invoice line",
            "SIGNED-CREDIT-EVIDENCE-D03-001");
        var result = Assess(f, proposals: new[] { correction });
        Assert.False(result.CanPreview);
        Assert.Contains("UnclassifiedMonthlyCorrectionCannotBeRent", result.Blockers);
    }

    [Fact]
    public void TRF_D03_10_Accepted_Total_Invoice_Credit_Still_Unclassified()
    {
        var f = Fixture();
        var correction = MonthlyInvoiceAdjustmentRecord.Propose(
            Guid.NewGuid(), f.Issued, Guid.NewGuid(), Now.AddHours(8),
            -50_000m, "An independently accepted but unclassified credit",
            "SIGNED-CREDIT-EVIDENCE-D03-002");
        var decision = MonthlyInvoiceAdjustmentDecisionRecord.Decide(
            Guid.NewGuid(), correction, f.Issued,
            Guid.NewGuid(), Now.AddHours(9), true,
            "Approved credit but unclear rent versus utility category");
        var result = Assess(f,
            proposals: new[] { correction }, decisions: new[] { decision });
        Assert.False(result.CanPreview);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void TRF_D03_11_Receipt_History_And_AsOf_Date_Must_Be_Complete()
    {
        var f = Fixture();
        var missing = RoomChangeOfficialSourceRentEvidenceBridge.Assess(
            f.Quote, f.Posting, f.Issued, f.Frozen, f.InvoiceReview,
            AsOf, null, new[] { f.Bank },
            Array.Empty<OfficialMonthlyReceiptLineAllocationRecord>(),
            Array.Empty<MonthlyInvoiceAdjustmentRecord>(),
            Array.Empty<MonthlyInvoiceAdjustmentDecisionRecord>());
        Assert.False(missing.CanPreview);
        Assert.Contains("CompleteSourceInvoiceAndReceiptHistoryRequired",
            missing.Blockers);
        Assert.False(Assess(f, asOf: new DateOnly(2026, 10, 14)).CanPreview);
    }

    [Fact]
    public void TRF_D03_12_Future_Posting_Is_Not_A_Valid_Past_Balance()
    {
        var f = Fixture();
        // The source invoice was posted on Oct 30 local time.
        Assert.False(Assess(f, asOf: new DateOnly(2026, 10, 15)).CanPreview);
    }

    [Fact]
    public void TRF_D03_13_Forged_Receipt_Amount_Cannot_Be_Paid_Rent()
    {
        var f = Fixture();
        var forged = f.Receipt with { Amount = 280_000m };
        var result = Assess(f,
            receipts: new[] { forged }, reviews: new[] { f.Bank },
            lines: FullSplit(f));
        Assert.False(result.CanPreview);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void TRF_D03_14_Unmatched_Adjustment_Decision_And_Partial_Proof_Denied()
    {
        var f = Fixture();
        var correction = MonthlyInvoiceAdjustmentRecord.Propose(
            Guid.NewGuid(), f.Issued, Guid.NewGuid(), Now.AddHours(8),
            50_000m, "Unclassified debit for issued monthly invoice",
            "SIGNED-DEBIT-EVIDENCE-D03-001");
        var decision = MonthlyInvoiceAdjustmentDecisionRecord.Decide(
            Guid.NewGuid(), correction, f.Issued,
            Guid.NewGuid(), Now.AddHours(9), false,
            "Rejected debit adjustment pending inspection");
        Assert.False(Assess(f, decisions: new[] { decision }).CanPreview);
        Assert.False(Assess(f,
            receipts: new[] { f.Receipt }, reviews: new[] { f.Bank },
            lines: Array.Empty<OfficialMonthlyReceiptLineAllocationRecord>())
            .CanPreview);
    }
    private static (
        MonthlyInvoiceAdjustmentRecord Proposal,
        MonthlyInvoiceAdjustmentDecisionRecord Decision)
        Correction(F f, decimal delta, bool accepted = true)
    {
        var proposal = MonthlyInvoiceAdjustmentRecord.Propose(
            Guid.NewGuid(), f.Issued, Guid.NewGuid(), Now.AddHours(8),
            delta, "Signed invoice correction classified by type of charge",
            "SIGNED-INVOICE-ADJUSTMENT-PROPOSAL-OCT2026");
        var decision = MonthlyInvoiceAdjustmentDecisionRecord.Decide(
            Guid.NewGuid(), proposal, f.Issued, Guid.NewGuid(),
            Now.AddHours(9), accepted,
            "Independently reviewed source invoice correction");
        return (proposal, decision);
    }

    private static RoomChangeIssuedAdjustmentCategoryProof Categorize(
        F f, MonthlyInvoiceAdjustmentRecord proposal,
        MonthlyInvoiceAdjustmentDecisionRecord decision,
        params RoomChangeIssuedAdjustmentCategoryLine[] lines)
        => RoomChangeIssuedAdjustmentCategoryProof.Capture(
            Guid.NewGuid(), f.Issued, proposal, decision, lines,
            Guid.NewGuid(), Now.AddHours(10),
            "SIGNED-OWNER-CLASSIFIED-CORRECTION-OCT2026");

    private static RoomChangeIssuedAdjustmentCategoryLine LineCorrection(
        ContractDepositOffsetCategory category, decimal amount)
        => new(category, amount, "SIGNED-ORIGINAL-CATEGORY-EVIDENCE-D03");

    [Fact]
    public void TRF_D03_18_Independent_Rent_Line_Credit_Is_Classified_Not_Posted()
    {
        var f = Fixture();
        var (p,d) = Correction(f, -100_000m);
        var review = Categorize(f, p, d,
            LineCorrection(ContractDepositOffsetCategory.UnpaidRent, -100_000m));
        review.Verify(f.Issued, p, d);
        var result = RoomChangeIssuedAdjustmentCategoryEvaluator.Assess(
            f.Issued, f.Frozen, f.InvoiceReview, AsOf,
            new[] { p }, new[] { d }, new[] { review });
        Assert.Equal(-100_000m, result.RentSignedDeltaVnd);
        Assert.Equal(0m, result.ElectricitySignedDeltaVnd);
        Assert.False(result.IsOfficialPosting);
        Assert.False(result.CanApplyCreditToRealDebt);
        Assert.False(result.CanExecuteRoomChange);
    }

    [Fact]
    public void TRF_D03_19_Utility_Only_Adjustment_Cannot_Launder_Rent_Credit()
    {
        var f = Fixture();
        var (p,d) = Correction(f, -40_000m);
        var classified = Categorize(f, p, d,
            LineCorrection(ContractDepositOffsetCategory.Electricity, -25_000m),
            LineCorrection(ContractDepositOffsetCategory.Water, -15_000m));
        var result = RoomChangeIssuedAdjustmentCategoryEvaluator.Assess(
            f.Issued, f.Frozen, f.InvoiceReview, AsOf,
            new[] { p }, new[] { d }, new[] { classified });
        Assert.Equal(0m, result.RentSignedDeltaVnd);
        Assert.Equal(-25_000m, result.ElectricitySignedDeltaVnd);
        Assert.Equal(-15_000m, result.WaterSignedDeltaVnd);
    }

    [Fact]
    public void TRF_D03_20_Missing_Amount_Or_Duplicate_Category_Refused()
    {
        var f = Fixture();
        var (p,d) = Correction(f, -50_000m);
        Assert.Throws<InvalidOperationException>(() =>
            Categorize(f, p, d,
                LineCorrection(ContractDepositOffsetCategory.UnpaidRent, -25_000m)));
        Assert.Throws<InvalidOperationException>(() =>
            Categorize(f, p, d,
                LineCorrection(ContractDepositOffsetCategory.UnpaidRent, -25_000m),
                LineCorrection(ContractDepositOffsetCategory.UnpaidRent, -25_000m)));
        Assert.Throws<InvalidOperationException>(() =>
            Categorize(f, p, d,
                LineCorrection(ContractDepositOffsetCategory.UnpaidRent, -60_000m),
                LineCorrection(ContractDepositOffsetCategory.Electricity, 10_000m)));
    }

    [Fact]
    public void TRF_D03_21_Missing_Decision_Or_Classified_Approval_Blocks()
    {
        var f = Fixture();
        var (p,d) = Correction(f, -50_000m);
        Assert.Throws<InvalidDataException>(() =>
            RoomChangeIssuedAdjustmentCategoryEvaluator.Assess(
                f.Issued, f.Frozen, f.InvoiceReview, AsOf,
                new[] { p }, Array.Empty<MonthlyInvoiceAdjustmentDecisionRecord>(),
                Array.Empty<RoomChangeIssuedAdjustmentCategoryProof>()));
        Assert.Throws<InvalidDataException>(() =>
            RoomChangeIssuedAdjustmentCategoryEvaluator.Assess(
                f.Issued, f.Frozen, f.InvoiceReview, AsOf, new[] { p }, new[] { d },
                Array.Empty<RoomChangeIssuedAdjustmentCategoryProof>()));
    }

    [Fact]
    public void TRF_D03_22_Rejected_Correction_Has_Zero_Effect_Without_Proof()
    {
        var f = Fixture();
        var (p,d) = Correction(f, -50_000m, accepted: false);
        var result = RoomChangeIssuedAdjustmentCategoryEvaluator.Assess(
            f.Issued, f.Frozen, f.InvoiceReview, AsOf, new[] { p }, new[] { d },
            Array.Empty<RoomChangeIssuedAdjustmentCategoryProof>());
        Assert.Equal(0m, result.RentSignedDeltaVnd);
        Assert.Equal(0, result.ClassifiedAdjustmentCount);
        Assert.Throws<InvalidOperationException>(() =>
            Categorize(f, p, d,
                LineCorrection(ContractDepositOffsetCategory.UnpaidRent, -50_000m)));
    }

    [Fact]
    public void TRF_D03_23_Another_Invoice_Or_Orphaned_Classified_Line_Fails()
    {
        var f = Fixture();
        var g = Fixture();
        var (p,d) = Correction(f, -30_000m);
        var classification = Categorize(f, p, d,
            LineCorrection(ContractDepositOffsetCategory.UnpaidRent, -30_000m));
        Assert.Throws<InvalidDataException>(() =>
            classification.Verify(g.Issued, p, d));
        Assert.Throws<InvalidDataException>(() =>
            RoomChangeIssuedAdjustmentCategoryEvaluator.Assess(
                f.Issued, f.Frozen, f.InvoiceReview, AsOf,
                Array.Empty<MonthlyInvoiceAdjustmentRecord>(),
                Array.Empty<MonthlyInvoiceAdjustmentDecisionRecord>(),
                new[] { classification }));
    }

    [Fact]
    public void TRF_D03_24_Classification_Cannot_Turn_Whole_Invoice_Negative()
    {
        var f = Fixture();
        var (p,d) = Correction(f, -3_050_000m);
        var classification = Categorize(f, p, d,
            LineCorrection(ContractDepositOffsetCategory.UnpaidRent, -3_000_000m),
            LineCorrection(ContractDepositOffsetCategory.Electricity, -35_000m),
            LineCorrection(ContractDepositOffsetCategory.Water, -15_000m));
        Assert.Throws<InvalidDataException>(() =>
            RoomChangeIssuedAdjustmentCategoryEvaluator.Assess(
                f.Issued, f.Frozen, f.InvoiceReview, AsOf,
                new[] { p }, new[] { d }, new[] { classification }));
    }

    [Fact]
    public void TRF_ARCH04_Classified_D03_Correction_Archives_Original_Issuance()
    {
        var f = Fixture();
        var (proposal, decision) = Correction(f, -20_000m);
        var correction = Categorize(f, proposal, decision,
            LineCorrection(ContractDepositOffsetCategory.UnpaidRent, -20_000m));
        var transfer = Transfer(f);
        var archive = RoomChangeEvidenceArchiveRecord.CaptureClassifiedCorrection(
            Guid.NewGuid(), transfer.Request,
            f.Issued, f.Frozen, f.InvoiceReview,
            proposal, decision, correction, Now.AddHours(11));
        archive.VerifyStoredSnapshot();
        Assert.Equal(RoomChangeEvidenceArchiveKind.ClassifiedInvoiceCorrection, archive.Kind);
        Assert.Equal(correction.Id, archive.EvidenceId);
        Assert.Equal(f.Issued.Id, archive.ParentEvidenceId);
        Assert.Equal(f.Issued.CandidateSha256, archive.ParentEvidenceSha256);
        Assert.False(archive.CanPostReceivable);
    }

    [Fact]
    public void TRF_ARCH05_Archive_Payload_Or_Source_Hash_Tampering_Detected()
    {
        var f = Fixture();
        var (proposal, decision) = Correction(f, -20_000m);
        var proof = Categorize(f, proposal, decision,
            LineCorrection(ContractDepositOffsetCategory.UnpaidRent, -20_000m));
        var transfer = Transfer(f);
        var archive = RoomChangeEvidenceArchiveRecord.CaptureClassifiedCorrection(
            Guid.NewGuid(), transfer.Request,
            f.Issued, f.Frozen, f.InvoiceReview,
            proposal, decision, proof, Now.AddHours(11));
        var field = typeof(RoomChangeEvidenceArchiveRecord).GetField(
            "<EvidenceJson>k__BackingField",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(archive, archive.EvidenceJson.Replace(
            "SIGNED-OWNER-CLASSIFIED-CORRECTION-OCT2026", "FORGED-CORRECTION-REF"));
        Assert.Throws<InvalidDataException>(() => archive.VerifyStoredSnapshot());
    }

}
