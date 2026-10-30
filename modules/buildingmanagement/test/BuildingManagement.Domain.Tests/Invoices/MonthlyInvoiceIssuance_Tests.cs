using System;
using System.IO;
using System.Linq;
using BuildingManagement.Invoices;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.Domain.Tests.Invoices;

public sealed class MonthlyInvoiceIssuance_Tests
{
    private static readonly DateTime PreparedAt =
        new(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MONTISS01_Independent_Approved_Review_Can_Issue_Staging_Bill()
    {
        var archive = Archive();
        var reviewer = Guid.NewGuid();
        var review = MonthlyInvoiceIndependentReviewRecord.Capture(
            Guid.NewGuid(), archive, reviewer, PreparedAt.AddHours(1),
            true, "Original rates and physical readings checked");
        var issuer = archive.ReviewedByUserId;
        var issued = MonthlyIssuedInvoiceRecord.Issue(
            Guid.NewGuid(), "NTM-202610-001", issuer,
            PreparedAt.AddHours(2), archive, review);

        issued.VerifyAgainst(archive, review);
        Assert.Equal(3_050_000m, issued.TotalAmount);
        Assert.Equal(archive.DueDate, issued.DueDate);
        Assert.Equal(archive.FrozenEvidenceSha256, issued.CandidateSha256);
        Assert.True(issued.IsStagingIssued);
        Assert.False(issued.IsOfficialReceivable);
        Assert.False(issued.HasIndependentlyVerifiedBankReceipt);
    }

    [Fact]
    public void MONTISS02_Self_Review_Fails_Closed()
    {
        var archive = Archive();
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyInvoiceIndependentReviewRecord.Capture(
                Guid.NewGuid(), archive, archive.ReviewedByUserId,
                PreparedAt.AddHours(1), true, "Not independent"));
    }

    [Fact]
    public void MONTISS03_Rejected_Review_Cannot_Issue()
    {
        var archive = Archive();
        var review = Review(archive, false);
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyIssuedInvoiceRecord.Issue(Guid.NewGuid(),
                "NTM-202610-002", archive.ReviewedByUserId,
                PreparedAt.AddHours(2), archive, review));
    }

    [Fact]
    public void MONTISS04_Issuer_Cannot_Be_Independent_Reviewer()
    {
        var archive = Archive();
        var review = Review(archive);
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyIssuedInvoiceRecord.Issue(Guid.NewGuid(),
                "NTM-202610-003", review.ReviewedByUserId,
                PreparedAt.AddHours(2), archive, review));
    }

    [Fact]
    public void MONTISS05_Changed_Archive_Or_Review_Is_Rejected()
    {
        var archive = Archive();
        var review = Review(archive);
        var issued = MonthlyIssuedInvoiceRecord.Issue(Guid.NewGuid(),
            "NTM-202610-004", archive.ReviewedByUserId,
            PreparedAt.AddHours(2), archive, review);
        typeof(MonthlyInvoiceIndependentReviewRecord)
            .GetProperty(nameof(MonthlyInvoiceIndependentReviewRecord.CandidateSha256))!
            .SetValue(review, new string('a', 64));

        Assert.Throws<InvalidDataException>(() =>
            issued.VerifyAgainst(archive, review));
    }

    [Fact]
    public void MONTISS06_Due_Date_Must_Be_On_Or_After_Issue_Date()
    {
        var archive = Archive();
        var review = Review(archive);
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyIssuedInvoiceRecord.Issue(Guid.NewGuid(),
                "NTM-202610-005", archive.ReviewedByUserId,
                new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc),
                archive, review));
    }

    [Fact]
    public void MONTISS18_Replayed_Issue_Cannot_Move_Past_Approved_Due_Date()
    {
        var archive = Archive();
        var review = Review(archive);
        var issued = MonthlyIssuedInvoiceRecord.Issue(
            Guid.NewGuid(), "NTM-202610-018", archive.ReviewedByUserId,
            PreparedAt.AddHours(2), archive, review);

        // Simulate a corrupted or rewritten persisted issuance timestamp.
        // Creation validation is insufficient: a reloaded record must
        // independently enforce the immutable due-date invariant.
        typeof(MonthlyIssuedInvoiceRecord)
            .GetProperty(nameof(MonthlyIssuedInvoiceRecord.IssuedAtUtc))!
            .SetValue(issued, new DateTime(
                2026, 11, 6, 0, 0, 0, DateTimeKind.Utc));

        Assert.Throws<InvalidDataException>(() =>
            issued.VerifyAgainst(archive, review));
    }

    [Fact]
    public void MONTISS07_Review_Must_Follow_Preparation_And_Have_Reason()
    {
        var archive = Archive();
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyInvoiceIndependentReviewRecord.Capture(
                Guid.NewGuid(), archive, Guid.NewGuid(),
                PreparedAt.AddSeconds(-1), true, "Source checked"));
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyInvoiceIndependentReviewRecord.Capture(
                Guid.NewGuid(), archive, Guid.NewGuid(),
                PreparedAt.AddHours(1), true, "short"));
    }

    [Fact]
    public void MONTISS08_UTC_Time_Roundtrips_Through_PostgreSQL_Precision()
    {
        var archive = Archive();
        var reviewer = Guid.NewGuid();
        var review = MonthlyInvoiceIndependentReviewRecord.Capture(
            Guid.NewGuid(), archive, reviewer,
            PreparedAt.AddHours(1).AddTicks(9), true,
            "Independent line item and rate approval");
        Assert.Equal(0L, review.ReviewedAtUtc.Ticks % 10L);
        var issued = MonthlyIssuedInvoiceRecord.Issue(
            Guid.NewGuid(), "NTM-202610-008", archive.ReviewedByUserId,
            PreparedAt.AddHours(2).AddTicks(7), archive, review);
        Assert.Equal(0L, issued.IssuedAtUtc.Ticks % 10L);
        issued.VerifyAgainst(archive, review);
    }

    [Fact]
    public void MONTISS09_Completed_Exact_Receipt_Can_Be_Staged_Once()
    {
        var archive = Archive();
        var review = Review(archive);
        var issued = MonthlyIssuedInvoiceRecord.Issue(Guid.NewGuid(),
            "NTM-202610-009", archive.ReviewedByUserId,
            PreparedAt.AddHours(2), archive, review);
        var receipt = Receipt(issued);
        var allocation = MonthlyInvoicePaymentAllocationRecord.Capture(
            Guid.NewGuid(), issued, receipt, archive.ReviewedByUserId,
            PreparedAt.AddHours(4));
        allocation.VerifySource(receipt);

        Assert.Equal(issued.Id, allocation.IssuedInvoiceId);
        Assert.Equal(500_000m, allocation.Amount);
        Assert.False(allocation.IsIndependentlyVerifiedBankPayment);
        Assert.False(allocation.IsPostedDebtSettlement);
        Assert.False(allocation.CanOffsetDeposit);
    }

    [Fact]
    public void MONTISS10_Foreign_Invoice_Or_Contract_Payment_Is_Rejected()
    {
        var archive = Archive();
        var review = Review(archive);
        var issued = MonthlyIssuedInvoiceRecord.Issue(Guid.NewGuid(),
            "NTM-202610-010", archive.ReviewedByUserId,
            PreparedAt.AddHours(2), archive, review);
        var foreign = new Payment(
            Guid.NewGuid(), "MONTH-PAY-FOREIGN", Guid.NewGuid(),
            PaymentPurpose.InvoicePayment, PaymentMethod.BankTransfer,
            PaymentChannel.Manual, 500_000m, contractId: issued.ContractId,
            invoiceId: Guid.NewGuid(),
            transactionReference: "BANK-MONTH-FOREIGN-001");
        foreign.Complete(PreparedAt.AddHours(3), "BANK-MONTH-FOREIGN-001");
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyInvoicePaymentAllocationRecord.Capture(
                Guid.NewGuid(), issued, foreign, Guid.NewGuid(),
                PreparedAt.AddHours(4)));
    }

    [Fact]
    public void MONTISS11_Mutated_Original_Receipt_Invalidates_Monthly_Allocation()
    {
        var archive = Archive();
        var review = Review(archive);
        var issued = MonthlyIssuedInvoiceRecord.Issue(Guid.NewGuid(),
            "NTM-202610-011", archive.ReviewedByUserId,
            PreparedAt.AddHours(2), archive, review);
        var receipt = Receipt(issued);
        var allocation = MonthlyInvoicePaymentAllocationRecord.Capture(
            Guid.NewGuid(), issued, receipt, Guid.NewGuid(),
            PreparedAt.AddHours(4));
        typeof(Payment).GetProperty(nameof(Payment.Amount))!
            .SetValue(receipt, 499_999m);
        Assert.Throws<InvalidOperationException>(() =>
            allocation.VerifySource(receipt));
    }

    [Fact]
    public void MONTISS12_Approved_Credit_Debit_And_Rejected_Note_Are_Audited()
    {
        var archive = Archive();
        var review = Review(archive);
        var issued = MonthlyIssuedInvoiceRecord.Issue(Guid.NewGuid(),
            "NTM-CORRECT-012", archive.ReviewedByUserId,
            PreparedAt.AddHours(2), archive, review);
        var credit = MonthlyInvoiceAdjustmentRecord.Propose(
            Guid.NewGuid(), issued, archive.ReviewedByUserId,
            PreparedAt.AddHours(3), -100_000m,
            "Incorrect recorded water charge needs credit",
            "SIGNED-WATER-RATE-CORRECTION-001");
        var debit = MonthlyInvoiceAdjustmentRecord.Propose(
            Guid.NewGuid(), issued, archive.ReviewedByUserId,
            PreparedAt.AddHours(4), 40_000m,
            "Documented missed internet service fee",
            "CONTRACT-INTERNET-FEE-ADDENDUM-001");
        var rejected = MonthlyInvoiceAdjustmentRecord.Propose(
            Guid.NewGuid(), issued, archive.ReviewedByUserId,
            PreparedAt.AddHours(5), 8_000m,
            "Unsupported extra parking monthly charge",
            "NO-SIGNED-PARKING-POLICY-001");
        var d1 = MonthlyInvoiceAdjustmentDecisionRecord.Decide(
            Guid.NewGuid(), credit, issued, Guid.NewGuid(),
            PreparedAt.AddHours(6), true, "Original meter comparison independently checked");
        var d2 = MonthlyInvoiceAdjustmentDecisionRecord.Decide(
            Guid.NewGuid(), debit, issued, Guid.NewGuid(),
            PreparedAt.AddHours(7), true, "Signed internet policy reviewed independently");
        var d3 = MonthlyInvoiceAdjustmentDecisionRecord.Decide(
            Guid.NewGuid(), rejected, issued, Guid.NewGuid(),
            PreparedAt.AddHours(8), false, "No authorized parking service reference exists");
        var result = MonthlyInvoiceAdjustmentEvaluator.Assess(
            issued, new[] { credit, debit, rejected }, new[] { d1, d2, d3 },
            new DateOnly(2026, 10, 31), 500_000m);
        Assert.Equal(-60_000m, result.ApprovedSignedAdjustment);
        Assert.Equal(2_990_000m, result.AdjustedAmount);
        Assert.Equal(2, result.ApprovedAdjustmentCount);
        Assert.False(result.IsOfficialDebtPosted);
        Assert.True(credit.IsCreditNote);
        Assert.True(debit.IsDebitNote);
        Assert.False(d3.Accepted);
    }

    [Fact]
    public void MONTISS13_Rejected_Or_Pending_Adjustment_Does_Not_Change_Debt()
    {
        var issued = IssuedForAdjustment();
        var proposal = MonthlyInvoiceAdjustmentRecord.Propose(
            Guid.NewGuid(), issued, Guid.NewGuid(), PreparedAt.AddHours(3),
            -50_000m, "Credit proposal still under independent review",
            "REVIEW-PENDING-CREDIT-001");
        var pending = MonthlyInvoiceAdjustmentEvaluator.Assess(
            issued, new[] { proposal },
            Array.Empty<MonthlyInvoiceAdjustmentDecisionRecord>(),
            new DateOnly(2026, 10, 30), 0m);
        Assert.Equal(issued.TotalAmount, pending.AdjustedAmount);
        var declined = MonthlyInvoiceAdjustmentDecisionRecord.Decide(
            Guid.NewGuid(), proposal, issued, Guid.NewGuid(),
            PreparedAt.AddHours(4), false, "Evidence was not sufficient for a credit");
        var rejected = MonthlyInvoiceAdjustmentEvaluator.Assess(
            issued, new[] { proposal }, new[] { declined },
            new DateOnly(2026, 10, 30), 0m);
        Assert.Equal(issued.TotalAmount, rejected.AdjustedAmount);
    }

    [Fact]
    public void MONTISS14_Self_Approval_And_Forged_History_Are_Rejected()
    {
        var issued = IssuedForAdjustment();
        var owner = Guid.NewGuid();
        var proposal = MonthlyInvoiceAdjustmentRecord.Propose(
            Guid.NewGuid(), issued, owner, PreparedAt.AddHours(3),
            -20_000m, "Correct previous double counted rent line",
            "SIGNED-CONTRACT-CORRECTION-002");
        Assert.Throws<InvalidOperationException>(() =>
            MonthlyInvoiceAdjustmentDecisionRecord.Decide(
                Guid.NewGuid(), proposal, issued, owner,
                PreparedAt.AddHours(4), true, "Invalid same actor"));
        var decision = MonthlyInvoiceAdjustmentDecisionRecord.Decide(
            Guid.NewGuid(), proposal, issued, Guid.NewGuid(),
            PreparedAt.AddHours(4), true, "Independent verification of original evidence");
        typeof(MonthlyInvoiceAdjustmentDecisionRecord)
            .GetProperty(nameof(MonthlyInvoiceAdjustmentDecisionRecord.ApprovedSignedDeltaAmount))!
            .SetValue(decision, -21_000m);
        Assert.Throws<InvalidDataException>(() =>
            MonthlyInvoiceAdjustmentEvaluator.Assess(issued,
                new[] { proposal }, new[] { decision },
                new DateOnly(2026, 10, 31), 0m));
    }

    [Fact]
    public void MONTISS15_Credit_Cannot_Erase_Already_Captured_Bank_Receipts()
    {
        var issued = IssuedForAdjustment();
        var proposal = MonthlyInvoiceAdjustmentRecord.Propose(
            Guid.NewGuid(), issued, Guid.NewGuid(),
            PreparedAt.AddHours(3), -100_000m,
            "Credit for incorrectly applied service charge",
            "SIGNED-CREDIT-EVIDENCE-003");
        var decision = MonthlyInvoiceAdjustmentDecisionRecord.Decide(
            Guid.NewGuid(), proposal, issued, Guid.NewGuid(),
            PreparedAt.AddHours(4), true,
            "Independent check supports correction amount");
        Assert.Throws<InvalidDataException>(() =>
            MonthlyInvoiceAdjustmentEvaluator.Assess(
                issued, new[] { proposal }, new[] { decision },
                new DateOnly(2026, 10, 31), issued.TotalAmount));
    }

    [Fact]
    public void MONTISS16_Adjustments_Apply_Only_On_Approval_Local_Date()
    {
        var issued = IssuedForAdjustment();
        var proposal = MonthlyInvoiceAdjustmentRecord.Propose(
            Guid.NewGuid(), issued, Guid.NewGuid(),
            new DateTime(2026, 10, 30, 16, 30, 0, DateTimeKind.Utc),
            60_000m, "Late recorded utility consumption evidence",
            "SIGNED-METER-RECONCILIATION-004");
        var decision = MonthlyInvoiceAdjustmentDecisionRecord.Decide(
            Guid.NewGuid(), proposal, issued, Guid.NewGuid(),
            new DateTime(2026, 10, 30, 17, 30, 0, DateTimeKind.Utc),
            true, "Independent review verified the meter evidence");
        var prior = MonthlyInvoiceAdjustmentEvaluator.Assess(
            issued, new[] { proposal }, new[] { decision },
            new DateOnly(2026, 10, 30), 0m);
        var today = MonthlyInvoiceAdjustmentEvaluator.Assess(
            issued, new[] { proposal }, new[] { decision },
            new DateOnly(2026, 10, 31), 0m);
        Assert.Equal(0m, prior.ApprovedSignedAdjustment);
        Assert.Equal(60_000m, today.ApprovedSignedAdjustment);
    }

    [Fact]
    public void MONTISS17_Orphaned_Duplicate_And_Cross_Invoice_Notes_Refused()
    {
        var issued = IssuedForAdjustment();
        var proposal = MonthlyInvoiceAdjustmentRecord.Propose(
            Guid.NewGuid(), issued, Guid.NewGuid(),
            PreparedAt.AddHours(3), 10_000m,
            "Original invoice had a missed charge",
            "SIGNED-ORIGINAL-INVOICE-REF-005");
        var decision = MonthlyInvoiceAdjustmentDecisionRecord.Decide(
            Guid.NewGuid(), proposal, issued, Guid.NewGuid(),
            PreparedAt.AddHours(4), true,
            "Independent review of missed charge");
        Assert.Throws<InvalidDataException>(() =>
            MonthlyInvoiceAdjustmentEvaluator.Assess(issued,
                Array.Empty<MonthlyInvoiceAdjustmentRecord>(),
                new[] { decision }, new DateOnly(2026, 10, 30), 0m));
        Assert.Throws<InvalidDataException>(() =>
            MonthlyInvoiceAdjustmentEvaluator.Assess(issued,
                new[] { proposal, proposal },
                new[] { decision }, new DateOnly(2026, 10, 30), 0m));
        var foreign = IssuedForAdjustment();
        Assert.Throws<InvalidDataException>(() =>
            MonthlyInvoiceAdjustmentEvaluator.Assess(foreign,
                new[] { proposal }, new[] { decision },
                new DateOnly(2026, 10, 30), 0m));
    }

    private static MonthlyIssuedInvoiceRecord IssuedForAdjustment()
    {
        var archive = Archive();
        var review = Review(archive);
        return MonthlyIssuedInvoiceRecord.Issue(
            Guid.NewGuid(), "NTM-CORRECT-" + Guid.NewGuid().ToString("N"),
            archive.ReviewedByUserId, PreparedAt.AddHours(2),
            archive, review);
    }

    private static Payment Receipt(MonthlyIssuedInvoiceRecord issued)
    {
        var receipt = new Payment(
            Guid.NewGuid(), "MONTH-PAY-EXACT", Guid.NewGuid(),
            PaymentPurpose.InvoicePayment, PaymentMethod.BankTransfer,
            PaymentChannel.Manual, 500_000m,
            contractId: issued.ContractId, invoiceId: issued.Id,
            transactionReference: "BANK-MONTH-EXACT-001");
        receipt.Complete(PreparedAt.AddHours(3), "BANK-MONTH-EXACT-001");
        return receipt;
    }

    private static MonthlyInvoiceIndependentReviewRecord Review(
        MonthlyInvoiceCandidateLedgerRecord archive, bool accepted = true)
        => MonthlyInvoiceIndependentReviewRecord.Capture(
            Guid.NewGuid(), archive, Guid.NewGuid(),
            PreparedAt.AddHours(1), accepted,
            "Reviewed source rates and contractual fee decisions");

    private static MonthlyInvoiceCandidateLedgerRecord Archive()
    {
        var lines = new[]
        {
            Line(UtilityType.Electricity, 10m, 3_500m, 100m),
            Line(UtilityType.Water, 1m, 15_000m, 40m)
        };
        var candidate = new MonthlyInvoiceCandidateSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            2026, 10, Guid.NewGuid(), PreparedAt,
            new DateOnly(2026, 11, 5), "APPROVED-MONTHLY-DUE-001",
            3_000_000m, 35_000m, 15_000m,
            AuxiliaryFeeAssessmentDecision.ExplicitlyNoCharges,
            "EXPLICIT-NO-AUXILIARY-SERVICES",
            Array.Empty<FinalInvoiceAuxiliaryChargeProposal>(),
            0m, 3_050_000m, lines);
        return MonthlyInvoiceCandidateLedgerRecord.Capture(
            Guid.NewGuid(), candidate);
    }

    private static MonthlyInvoiceCandidateUtilityLine Line(
        UtilityType kind, decimal quantity, decimal price, decimal previous)
        => new(kind, Guid.NewGuid(), quantity, price, quantity * price,
            new[] { new MonthlyInvoiceCandidateMeterSource(
                Guid.NewGuid(), Guid.NewGuid(),
                MeterUsageSourceType.MonthlyReading,
                new DateTime(2026, 10, 31), previous, previous + quantity) });
}
