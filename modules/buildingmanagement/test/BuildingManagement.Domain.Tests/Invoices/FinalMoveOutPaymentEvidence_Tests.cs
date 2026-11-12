using System;
using System.Linq;
using BuildingManagement.Contracts;
using BuildingManagement.Invoices;
using BuildingManagement.MoveOuts;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.Utilities;
using Xunit;

namespace BuildingManagement.Domain.Tests.Invoices;

public sealed class FinalMoveOutPaymentEvidence_Tests
{
    [Fact]
    public void FINPAY01_No_Receipt_Is_Not_Paid_Debt()
    {
        var review = Review();
        var evidence = FinalMoveOutPaymentEvidenceEvaluator.Assess(
            review, Array.Empty<Payment>());

        Assert.Equal(0m, evidence.ObservedCompletedReceiptAmount);
        Assert.Equal(0m, evidence.ObservedPendingReceiptAmount);
        Assert.Null(evidence.OutstandingDebt);
        Assert.False(evidence.IsDebtReconciled);
        Assert.False(evidence.CanSettleDeposit);
        Assert.Contains("FinalInvoiceNotIssued", evidence.Blockers);
        Assert.Contains("PaymentAllocationNotImplemented", evidence.Blockers);
    }

    [Fact]
    public void FINPAY02_Completed_Receipt_Is_Observed_But_Never_Allocated_To_Draft()
    {
        var review = Review();
        var payment = Receipt(review, PaymentPurpose.InvoicePayment);
        payment.Complete(DateTime.UtcNow, "FINPAY-02");
        var evidence = FinalMoveOutPaymentEvidenceEvaluator.Assess(
            review, new[] { payment });

        Assert.Equal(1, evidence.ObservedReceiptCount);
        Assert.Equal(50000m, evidence.ObservedCompletedReceiptAmount);
        Assert.Contains("CompletedPaymentLinkedToUnissuedDraft", evidence.Blockers);
        Assert.Null(evidence.OutstandingDebt);
        Assert.False(evidence.IsDebtReconciled);
    }

    [Fact]
    public void FINPAY03_Deposit_Labeled_As_Invoice_Is_An_Anomaly()
    {
        var review = Review();
        var deposit = Receipt(review, PaymentPurpose.ContractDeposit);
        deposit.Complete(DateTime.UtcNow, "FINPAY-03");
        var evidence = FinalMoveOutPaymentEvidenceEvaluator.Assess(
            review, new[] { deposit });

        Assert.Contains("InvoicePaymentAttributionInvalid", evidence.Blockers);
        Assert.Null(evidence.ObservedCompletedReceiptAmount);
        Assert.Null(evidence.OutstandingDebt);
    }

    [Fact]
    public void FINPAY04_Pending_And_Cancelled_Are_Not_Completed_Receipts()
    {
        var review = Review();
        var pending = Receipt(review, PaymentPurpose.InvoicePayment);
        var cancelled = Receipt(review, PaymentPurpose.InvoicePayment);
        cancelled.Cancel();
        var evidence = FinalMoveOutPaymentEvidenceEvaluator.Assess(
            review, new[] { pending, cancelled });

        Assert.Equal(0m, evidence.ObservedCompletedReceiptAmount);
        Assert.Equal(50000m, evidence.ObservedPendingReceiptAmount);
        Assert.Null(evidence.OutstandingDebt);
    }

    [Fact]
    public void FINPAY05_Unallocated_Contract_Payment_Requires_Review()
    {
        var review = Review();
        var unallocated = Receipt(review, PaymentPurpose.InvoicePayment,
            invoiceId: null, omitInvoiceLink: true);
        unallocated.Complete(DateTime.UtcNow);
        var evidence = FinalMoveOutPaymentEvidenceEvaluator.Assess(
            review, new[] { unallocated });

        Assert.Contains("UnallocatedInvoicePayment", evidence.Blockers);
        Assert.Contains("InvoicePaymentAttributionInvalid", evidence.Blockers);
        Assert.Null(evidence.ObservedCompletedReceiptAmount);
    }

    [Fact]
    public void FINPAY06_Cross_Contract_And_Duplicate_Identifiers_Are_Rejected()
    {
        var review = Review();
        var foreign = Receipt(review, PaymentPurpose.InvoicePayment,
            contractId: Guid.NewGuid());
        foreign.Complete(DateTime.UtcNow);
        var other = FinalMoveOutPaymentEvidenceEvaluator.Assess(
            review, new[] { foreign });
        Assert.Contains("InvoicePaymentAttributionInvalid", other.Blockers);

        var legitimate = Receipt(review, PaymentPurpose.InvoicePayment);
        var duplicated = FinalMoveOutPaymentEvidenceEvaluator.Assess(
            review, new[] { legitimate, legitimate });
        Assert.Contains("DuplicatePaymentIdentifier", duplicated.Blockers);
        Assert.Null(duplicated.ObservedPendingReceiptAmount);
    }

    [Fact]
    public void FINPAY07_Older_Invoice_And_Normal_Deposit_Are_Not_Reused()
    {
        var review = Review();
        var otherMonth = Receipt(review, PaymentPurpose.InvoicePayment,
            invoiceId: Guid.NewGuid());
        otherMonth.Complete(DateTime.UtcNow);
        var deposit = Receipt(review, PaymentPurpose.ContractDeposit,
            invoiceId: null, omitInvoiceLink: true);
        deposit.Complete(DateTime.UtcNow);
        var evidence = FinalMoveOutPaymentEvidenceEvaluator.Assess(
            review, new[] { otherMonth, deposit });

        Assert.Equal(0, evidence.ObservedReceiptCount);
        Assert.Equal(0m, evidence.ObservedCompletedReceiptAmount);
        Assert.Null(evidence.OutstandingDebt);
    }

    [Fact]
    public void FINPAY08_Duplicate_Transaction_References_Are_Reviewed()
    {
        var review = Review();
        var first = Receipt(review, PaymentPurpose.InvoicePayment);
        var second = Receipt(review, PaymentPurpose.InvoicePayment);
        first.Complete(DateTime.UtcNow, "SAME-REFERENCE");
        second.Complete(DateTime.UtcNow, "same-reference");
        var evidence = FinalMoveOutPaymentEvidenceEvaluator.Assess(
            review, new[] { first, second });

        Assert.Contains("DuplicateTransactionReference", evidence.Blockers);
        Assert.Null(evidence.ObservedCompletedReceiptAmount);
    }

    private static Payment Receipt(
        FinalMoveOutBillReview review, PaymentPurpose purpose,
        Guid? invoiceId = null, Guid? contractId = null,
        bool omitInvoiceLink = false)
        => new(
            Guid.NewGuid(), "FINPAY-" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid(), purpose, PaymentMethod.Cash,
            PaymentChannel.Manual, 50000m, null,
            contractId ?? review.ContractId,
            omitInvoiceLink ? null : invoiceId ?? review.UtilityDraftId);

    private static FinalMoveOutBillReview Review()
    {
        var roomId = Guid.NewGuid();
        var contract = new Contract(Guid.NewGuid(), "FINPAY-CONTRACT",
            roomId, new DateTime(2026, 1, 1), null, 3000000m);
        contract.Activate();
        var request = new MoveOutRequest(Guid.NewGuid(), contract.Id, roomId,
            new DateOnly(2026, 11, 30), null);
        request.Approve(DateTime.UtcNow, null);

        var lines = new[] { UtilityType.Electricity, UtilityType.Water }
            .Select(type =>
            {
                var segment = new InvoiceDraftMeterSegmentSnapshot(
                    Guid.NewGuid(), Guid.NewGuid(),
                    MeterUsageSourceType.MonthlyReading,
                    new DateTime(2026, 11, 30), 0m, 10m);
                return new InvoiceDraftUtilityLineSnapshot(
                    type, Guid.NewGuid(), "SingleRateWholeMonth",
                    10m, 1000m, 10000m, new[] { segment },
                    Array.Empty<string>());
            });
        var snapshot = InvoiceDraftSnapshot.Capture(
            contract.Id, roomId, 2026, 11, lines);
        var draft = Invoice.CreateDraft(Guid.NewGuid(), snapshot, Guid.NewGuid);
        return FinalMoveOutBillReviewEvaluator.Assess(
            request, contract, new[] { draft }, draft.Items.ToArray(),
            draft.Items.SelectMany(x => x.MeterSources).ToArray());
    }
}
