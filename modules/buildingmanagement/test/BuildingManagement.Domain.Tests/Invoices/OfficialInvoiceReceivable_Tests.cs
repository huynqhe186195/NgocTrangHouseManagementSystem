using System;
using System.IO;
using System.Linq;
using BuildingManagement.Invoices;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.Domain.Tests.Invoices;

public sealed class OfficialInvoiceReceivable_Tests
{
    private static readonly DateTime Start =
        new(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OFFICIAL01_Original_Frozen_Monthly_Bill_Can_Be_Recognized_Without_Rewriting()
    {
        var (source, review, issued) = MonthlySource();
        var record = OfficialInvoiceReceivablePostingRecord.PostMonthly(
            Guid.NewGuid(), issued, source, review, Guid.NewGuid(),
            Start.AddHours(3));
        record.VerifyMonthly(issued, source, review);
        Assert.Equal(3_050_000m, record.OriginalAmount);
        Assert.True(record.IsOfficialReceivable);
        Assert.False(record.CanOffsetDeposit);
        Assert.False(issued.IsOfficialReceivable);
    }

    [Fact]
    public void OFFICIAL02_Only_Independently_Reviewed_Receipt_Reduces_Official_Debt()
    {
        var (_, _, issued) = MonthlySource();
        var posting = PostingFor(issued);
        var evidence = ReceiptFor(issued);
        var unreviewed = OfficialInvoiceReceivableBalanceEvaluator.Assess(
            posting, new DateOnly(2026, 10, 31), 0m,
            new[] { evidence }, Array.Empty<OfficialInvoiceReceiptReviewRecord>());
        Assert.Equal(posting.OriginalAmount, unreviewed.OutstandingAmount);
        Assert.Equal(1, unreviewed.PendingOrRejectedPaymentCount);
        var review = OfficialInvoiceReceiptReviewRecord.Review(
            Guid.NewGuid(), posting, evidence, Guid.NewGuid(),
            Start.AddHours(7), true, "SIGNED-BANK-STATEMENT-001",
            "Independently reconciled bank statement and amount");
        var recognized = OfficialInvoiceReceivableBalanceEvaluator.Assess(
            posting, new DateOnly(2026, 10, 31), 0m,
            new[] { evidence }, new[] { review });
        Assert.Equal(500_000m, recognized.IndependentlyReviewedManualPayments);
        Assert.Equal(2_550_000m, recognized.OutstandingAmount);
        Assert.True(recognized.IsOfficialDebtPosted);
        Assert.False(recognized.IsExternallyVerifiedBankTransfer);
        Assert.False(review.CanOffsetDeposit);
    }

    [Fact]
    public void OFFICIAL03_Self_Review_Cannot_Recognize_Manual_Bank_Receipt()
    {
        var (_, _, issued) = MonthlySource();
        var posting = PostingFor(issued);
        var evidence = ReceiptFor(issued);
        Assert.Throws<InvalidOperationException>(() =>
            OfficialInvoiceReceiptReviewRecord.Review(
                Guid.NewGuid(), posting, evidence,
                evidence.RecordedByUserId, Start.AddHours(7),
                true, "SIGNED-BANK-STATEMENT-001",
                "Self-review must be prohibited by policy"));
    }

    [Fact]
    public void OFFICIAL04_Duplicate_Or_Orphaned_Receipt_Review_Is_Rejected()
    {
        var (_, _, issued) = MonthlySource();
        var posting = PostingFor(issued);
        var evidence = ReceiptFor(issued);
        var approved = OfficialInvoiceReceiptReviewRecord.Review(
            Guid.NewGuid(), posting, evidence, Guid.NewGuid(),
            Start.AddHours(7), true, "SIGNED-BANK-STATEMENT-002",
            "Reviewed the independent original bank statement");
        Assert.Throws<InvalidDataException>(() =>
            OfficialInvoiceReceivableBalanceEvaluator.Assess(
                posting, new DateOnly(2026, 10, 31), 0m,
                new[] { evidence }, new[] { approved, approved }));
        Assert.Throws<InvalidDataException>(() =>
            OfficialInvoiceReceivableBalanceEvaluator.Assess(
                posting, new DateOnly(2026, 10, 31), 0m,
                Array.Empty<OfficialInvoiceReceiptEvidence>(),
                new[] { approved }));
    }

    [Fact]
    public void OFFICIAL05_Approved_Credit_Cannot_Erase_Verified_Money()
    {
        var (_, _, issued) = MonthlySource();
        var posting = PostingFor(issued);
        var evidence = ReceiptFor(issued);
        var approved = OfficialInvoiceReceiptReviewRecord.Review(
            Guid.NewGuid(), posting, evidence, Guid.NewGuid(),
            Start.AddHours(7), true, "SIGNED-BANK-STATEMENT-003",
            "Manual source bank transaction independently reviewed");
        Assert.Throws<InvalidDataException>(() =>
            OfficialInvoiceReceivableBalanceEvaluator.Assess(
                posting, new DateOnly(2026, 10, 31),
                -2_700_000m, new[] { evidence }, new[] { approved }));
    }

    [Fact]
    public void OFFICIAL06_Rejected_Manual_Bank_Review_Never_Settles_Debt()
    {
        var (_, _, issued) = MonthlySource();
        var posting = PostingFor(issued);
        var evidence = ReceiptFor(issued);
        var rejected = OfficialInvoiceReceiptReviewRecord.Review(
            Guid.NewGuid(), posting, evidence, Guid.NewGuid(),
            Start.AddHours(7), false, string.Empty,
            "Evidence did not establish actual completed bank transfer");
        var balance = OfficialInvoiceReceivableBalanceEvaluator.Assess(
            posting, new DateOnly(2026, 10, 31), 0m,
            new[] { evidence }, new[] { rejected });
        Assert.Equal(posting.OriginalAmount, balance.OutstandingAmount);
        Assert.Equal(0, balance.AcceptedPaymentCount);
        Assert.Equal(1, balance.PendingOrRejectedPaymentCount);
    }

    private static OfficialInvoiceReceivablePostingRecord PostingFor(
        MonthlyIssuedInvoiceRecord issued)
    {
        // Recreate the identical immutable source for the same issuance
        // using the shared issuance from MonthlySource overload below.
        var source = SourceLookup.Source!;
        var review = SourceLookup.Review!;
        return OfficialInvoiceReceivablePostingRecord.PostMonthly(
            Guid.NewGuid(), issued, source, review,
            Guid.NewGuid(), Start.AddHours(3));
    }

    private static OfficialInvoiceReceiptEvidence ReceiptFor(
        MonthlyIssuedInvoiceRecord issued)
    {
        var receipt = new Payment(Guid.NewGuid(),
            "MONTH-OFFICIAL-RECEIPT", Guid.NewGuid(),
            PaymentPurpose.InvoicePayment, PaymentMethod.BankTransfer,
            PaymentChannel.Manual, 500_000m,
            contractId: issued.ContractId, invoiceId: issued.Id,
            transactionReference: "BANK-OFFICIAL-RECEIPT-001",
            notes: "BANK-PROOF: SIGNED-ORIGINAL-EVIDENCE-001");
        receipt.Complete(Start.AddHours(4), "BANK-OFFICIAL-RECEIPT-001");
        var allocation = MonthlyInvoicePaymentAllocationRecord.Capture(
            Guid.NewGuid(), issued, receipt, Guid.NewGuid(),
            Start.AddHours(5));
        return OfficialInvoiceReceiptEvidence.FromMonthly(allocation, receipt);
    }

    private static class SourceLookup
    {
        [ThreadStatic] public static MonthlyInvoiceCandidateLedgerRecord? Source;
        [ThreadStatic] public static MonthlyInvoiceIndependentReviewRecord? Review;
    }

    private static (MonthlyInvoiceCandidateLedgerRecord Source,
        MonthlyInvoiceIndependentReviewRecord Review,
        MonthlyIssuedInvoiceRecord Issued) MonthlySource()
    {
        var kind = Enum.GetValues<MeterUsageSourceType>().First();
        var electric = new MonthlyInvoiceCandidateUtilityLine(
            UtilityType.Electricity, Guid.NewGuid(), 10m, 3_500m, 35_000m,
            new[] { new MonthlyInvoiceCandidateMeterSource(
                Guid.NewGuid(), Guid.NewGuid(), kind,
                new DateTime(2026, 10, 30), 100m, 110m) });
        var water = new MonthlyInvoiceCandidateUtilityLine(
            UtilityType.Water, Guid.NewGuid(), 1m, 15_000m, 15_000m,
            new[] { new MonthlyInvoiceCandidateMeterSource(
                Guid.NewGuid(), Guid.NewGuid(), kind,
                new DateTime(2026, 10, 30), 1m, 2m) });
        var candidate = new MonthlyInvoiceCandidateSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            2026, 10, Guid.NewGuid(), Start,
            new DateOnly(2026, 11, 5), "MONTHLY-DUE-DECISION-001",
            3_000_000m, 35_000m, 15_000m,
            AuxiliaryFeeAssessmentDecision.ExplicitlyNoCharges,
            "NO-AUXILIARY-FEES-APPROVED-001",
            Array.Empty<FinalInvoiceAuxiliaryChargeProposal>(),
            0m, 3_050_000m, new[] { electric, water });
        var source = MonthlyInvoiceCandidateLedgerRecord.Capture(
            Guid.NewGuid(), candidate);
        var review = MonthlyInvoiceIndependentReviewRecord.Capture(
            Guid.NewGuid(), source, Guid.NewGuid(), Start.AddHours(1),
            true, "Independent source evidence approved for billing");
        var issued = MonthlyIssuedInvoiceRecord.Issue(
            Guid.NewGuid(), "NTM-OFFICIAL-001", source.ReviewedByUserId,
            Start.AddHours(2), source, review);
        SourceLookup.Source = source;
        SourceLookup.Review = review;
        return (source, review, issued);
    }
}
