using System;
using System.Linq;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Invoices;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.DomainTests.DepositSettlements;

public sealed class OfficialMonthlyReceiptLineAllocation_Tests
{
    private static readonly DateTime Now =
        new(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly AsOf = new(2026, 10, 31);

    private static (OfficialInvoiceReceivablePostingRecord Post,
        MonthlyInvoiceCandidateLedgerRecord Source,
        MonthlyIssuedInvoiceRecord Issued,
        MonthlyInvoiceIndependentReviewRecord Review,
        OfficialInvoiceReceiptEvidence Receipt,
        OfficialInvoiceReceiptReviewRecord Bank) Fixture()
    {
        var type = Enum.GetValues<MeterUsageSourceType>().First();
        MonthlyInvoiceCandidateUtilityLine Utility(
            UtilityType u, decimal quantity, decimal unit,
            decimal before, decimal after) =>
            new(u, Guid.NewGuid(), quantity, unit, quantity * unit,
                new[] { new MonthlyInvoiceCandidateMeterSource(
                    Guid.NewGuid(), Guid.NewGuid(), type,
                    new DateTime(2026, 10, 30), before, after) });
        var candidate = new MonthlyInvoiceCandidateSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            2026, 10, Guid.NewGuid(), Now,
            new DateOnly(2026, 11, 5), "SIGNED-MONTHLY-DUE-2026",
            3_000_000m, 35_000m, 15_000m,
            AuxiliaryFeeAssessmentDecision.ExplicitlyNoCharges,
            "SIGNED-MONTHLY-NO-SERVICE-FEE",
            Array.Empty<FinalInvoiceAuxiliaryChargeProposal>(), 0m,
            3_050_000m, new[] {
                Utility(UtilityType.Electricity, 10m, 3500m, 100m, 110m),
                Utility(UtilityType.Water, 1m, 15000m, 10m, 11m) });
        var source = MonthlyInvoiceCandidateLedgerRecord.Capture(
            Guid.NewGuid(), candidate);
        var review = MonthlyInvoiceIndependentReviewRecord.Capture(
            Guid.NewGuid(), source, Guid.NewGuid(), Now.AddHours(1), true,
            "Independent monthly financial review for line allocation");
        var issued = MonthlyIssuedInvoiceRecord.Issue(
            Guid.NewGuid(), "NT-MONTH-SPLIT-001", source.ReviewedByUserId,
            Now.AddHours(2), source, review);
        var posting = OfficialInvoiceReceivablePostingRecord.PostMonthly(
            Guid.NewGuid(), issued, source, review, Guid.NewGuid(),
            Now.AddHours(3));
        var payment = new Payment(Guid.NewGuid(), "PAY-SPLIT-001",
            Guid.NewGuid(), PaymentPurpose.InvoicePayment,
            PaymentMethod.BankTransfer, PaymentChannel.Manual, 250_000m,
            contractId: issued.ContractId, invoiceId: issued.Id,
            transactionReference: "BANK-SPLIT-RECEIPT-001",
            notes: "BANK-PROOF: SIGNED-BANK-STATEMENT-SPLIT-001");
        payment.Complete(Now.AddHours(4), "BANK-SPLIT-RECEIPT-001");
        var journal = MonthlyInvoicePaymentAllocationRecord.Capture(
            Guid.NewGuid(), issued, payment, Guid.NewGuid(), Now.AddHours(5));
        var receipt = OfficialInvoiceReceiptEvidence.FromMonthly(journal, payment);
        var bank = OfficialInvoiceReceiptReviewRecord.Review(
            Guid.NewGuid(), posting, receipt, Guid.NewGuid(),
            Now.AddHours(6), true, "SIGNED-BANK-STATEMENT-SPLIT-001",
            "Independent statement and payment amount verified");
        return (posting, source, issued, review, receipt, bank);
    }

    private static OfficialMonthlyReceiptLineAllocationRecord Line(
        (OfficialInvoiceReceivablePostingRecord Post,
         MonthlyInvoiceCandidateLedgerRecord Source,
         MonthlyIssuedInvoiceRecord Issued,
         MonthlyInvoiceIndependentReviewRecord Review,
         OfficialInvoiceReceiptEvidence Receipt,
         OfficialInvoiceReceiptReviewRecord Bank) f,
        ContractDepositOffsetCategory category, decimal amount)
        => OfficialMonthlyReceiptLineAllocationRecord.Capture(
            Guid.NewGuid(), f.Post, f.Receipt, f.Bank, category, amount,
            Guid.NewGuid(), Now.AddHours(7),
            "SIGNED-OWNER-APPROVED-LINE-ALLOCATION-001");

    [Fact]
    public void DEP_LINE01_Reviewed_Partial_Receipt_May_Be_Split_Without_Invented_Priority()
    {
        var f = Fixture();
        var lines = new[] {
            Line(f, ContractDepositOffsetCategory.UnpaidRent, 200_000m),
            Line(f, ContractDepositOffsetCategory.Electricity, 35_000m),
            Line(f, ContractDepositOffsetCategory.Water, 15_000m) };
        var result = ContractDepositOfficialMonthlyClaimBridge.Assess(
            f.Post, f.Issued, f.Source, f.Review, AsOf,
            new[] { f.Receipt }, new[] { f.Bank },
            Array.Empty<MonthlyInvoiceAdjustmentRecord>(),
            Array.Empty<MonthlyInvoiceAdjustmentDecisionRecord>(), lines);
        Assert.True(result.CanPreview, string.Join(",", result.Blockers));
        Assert.Single(result.Claims);
        Assert.Equal(2_800_000m, result.Claims[0].OutstandingVnd);
        Assert.Equal(ContractDepositOffsetCategory.UnpaidRent, result.Claims[0].Category);
        Assert.False(result.CanPostOffset);
    }

    [Fact]
    public void DEP_LINE02_Incomplete_Receipt_Split_Blocks_All_Debt_Claims()
    {
        var f = Fixture();
        var result = OfficialMonthlyReceiptLineAllocationEvaluator.Assess(
            f.Post, f.Source, AsOf,
            new[] { f.Receipt }, new[] { f.Bank },
            new[] { Line(f, ContractDepositOffsetCategory.UnpaidRent, 200_000m) });
        Assert.Contains("IncompleteOrOverAllocatedMonthlyReceipt", result.Blockers);
        Assert.Null(result.PaidRentVnd);
    }

    [Fact]
    public void DEP_LINE03_Duplicate_Category_Or_Overused_Original_Receipt_Denied()
    {
        var f = Fixture();
        var a = Line(f, ContractDepositOffsetCategory.UnpaidRent, 125_000m);
        var b = Line(f, ContractDepositOffsetCategory.UnpaidRent, 125_000m);
        var result = OfficialMonthlyReceiptLineAllocationEvaluator.Assess(
            f.Post, f.Source, AsOf,
            new[] { f.Receipt }, new[] { f.Bank }, new[] { a, b });
        Assert.Contains("DuplicateOrMissingMonthlyReceiptLineEvidence", result.Blockers);
    }

    [Fact]
    public void DEP_LINE04_Amount_Cannot_Exceed_Archived_Utility_Line()
    {
        var f = Fixture();
        var result = OfficialMonthlyReceiptLineAllocationEvaluator.Assess(
            f.Post, f.Source, AsOf,
            new[] { f.Receipt }, new[] { f.Bank },
            new[] { Line(f, ContractDepositOffsetCategory.Electricity, 250_000m) });
        Assert.Contains("MonthlyLineAmountExceedsFrozenInvoice", result.Blockers);
    }

    [Fact]
    public void DEP_LINE05_Unreviewed_Or_Rejected_Bank_Evidence_Is_Not_A_Line_Claim()
    {
        var f = Fixture();
        var lines = new[] { Line(f, ContractDepositOffsetCategory.UnpaidRent, 250_000m) };
        var unreviewed = OfficialMonthlyReceiptLineAllocationEvaluator.Assess(
            f.Post, f.Source, AsOf,
            new[] { f.Receipt }, Array.Empty<OfficialInvoiceReceiptReviewRecord>(), lines);
        Assert.Contains("UnreviewedOrCrossInvoicePayment", unreviewed.Blockers);
        var rejected = OfficialInvoiceReceiptReviewRecord.Review(
            Guid.NewGuid(), f.Post, f.Receipt, Guid.NewGuid(),
            Now.AddHours(6), false, "",
            "Independent source statement did not confirm transfer");
        Assert.Throws<InvalidOperationException>(() =>
            OfficialMonthlyReceiptLineAllocationRecord.Capture(
                Guid.NewGuid(), f.Post, f.Receipt, rejected,
                ContractDepositOffsetCategory.UnpaidRent, 250_000m,
                Guid.NewGuid(), Now.AddHours(7),
                "SIGNED-ALLOCATION-REJECTED-001"));
    }

    [Fact]
    public void DEP_LINE06_Future_Signed_Split_And_Unknown_Category_Denied()
    {
        var f = Fixture();
        var line = Line(f, ContractDepositOffsetCategory.UnpaidRent, 250_000m);
        var past = OfficialMonthlyReceiptLineAllocationEvaluator.Assess(
            f.Post, f.Source, new DateOnly(2026, 10, 29),
            new[] { f.Receipt }, new[] { f.Bank }, new[] { line });
        Assert.False(past.CanClassifyRemainingDebt);
        Assert.Throws<InvalidOperationException>(() =>
            Line(f, ContractDepositOffsetCategory.ApprovedDamage, 250_000m));
    }

    [Fact]
    public void DEP_LINE07_Only_Independent_Actor_May_Sign_Allotment()
    {
        var f = Fixture();
        Assert.Throws<InvalidOperationException>(() =>
            OfficialMonthlyReceiptLineAllocationRecord.Capture(
                Guid.NewGuid(), f.Post, f.Receipt, f.Bank,
                ContractDepositOffsetCategory.UnpaidRent, 250_000m,
                f.Bank.ReviewedByUserId, Now.AddHours(7),
                "SIGNED-ALLOCATION-SELF-REVIEW-001"));
    }
    [Fact]
    public void DEP_LINE08_ReceiptRecorderCanAllocateAfterIndependentBankReview()
    {
        var f = Fixture();
        var signer = f.Receipt.RecordedByUserId;
        Assert.NotEqual(signer, f.Bank.ReviewedByUserId);
        var line = OfficialMonthlyReceiptLineAllocationRecord.Capture(
            Guid.NewGuid(), f.Post, f.Receipt, f.Bank,
            ContractDepositOffsetCategory.UnpaidRent, 250_000m,
            signer, Now.AddHours(7),
            "SIGNED-OWNER-AFTER-INDEPENDENT-BANK-REVIEW");
        line.VerifyAgainst(f.Post, f.Receipt, f.Bank);
        Assert.False(line.CanPostDebtOffset);
    }

}
