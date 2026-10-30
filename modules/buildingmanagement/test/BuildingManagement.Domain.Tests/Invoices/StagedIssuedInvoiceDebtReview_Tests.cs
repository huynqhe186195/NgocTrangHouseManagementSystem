using System;
using System.Linq;
using BuildingManagement.Invoices;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using Xunit;

namespace BuildingManagement.Domain.Tests.Invoices;

public sealed class StagedIssuedInvoiceDebtReview_Tests
{
    private static readonly DateOnly Due = new(2026, 11, 5);
    private static readonly DateOnly BeforeDue = new(2026, 11, 4);
    private static readonly DateOnly AfterDue = new(2026, 11, 6);

    [Fact]
    public void STAGEDEBT01_Only_Journaled_Completed_Payments_Produce_Partial_Preview()
    {
        var (invoice, contract) = (Guid.NewGuid(), Guid.NewGuid());
        var payment = Receipt(invoice, contract, 250_000m);
        payment.Complete(new DateTime(2026, 11, 4, 4, 0, 0, DateTimeKind.Utc));
        var result = Assess(invoice, contract,
            new[] { Entry(payment, invoice) }, new[] { payment }, BeforeDue);

        Assert.True(result.HasCompleteStagedEvidence);
        Assert.Equal(IssuedInvoicePaymentStatus.PartiallyPaid,
            result.StagedPaymentSummary!.Status);
        Assert.Equal(750_000m, result.StagedPaymentSummary.OutstandingBeforeAdjustments);
        Assert.False(result.IsOfficialReceivable);
        Assert.False(result.IsPostedAllocation);
        Assert.False(result.CanReduceDebt);
        Assert.False(result.CanSettleDeposit);
    }

    [Fact]
    public void STAGEDEBT02_Receipt_After_AsOf_Does_Not_Reduce_Historical_Balance()
    {
        var (invoice, contract) = (Guid.NewGuid(), Guid.NewGuid());
        var payment = Receipt(invoice, contract, 1_000_000m);
        payment.Complete(new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc));
        var entries = new[] { Entry(payment, invoice) };
        var historical = Assess(invoice, contract, entries, new[] { payment }, BeforeDue);
        var current = Assess(invoice, contract, entries, new[] { payment }, Due);

        Assert.Equal(IssuedInvoicePaymentStatus.Unpaid,
            historical.StagedPaymentSummary!.Status);
        Assert.Equal(1_000_000m,
            historical.StagedPaymentSummary.OutstandingBeforeAdjustments);
        Assert.Equal(IssuedInvoicePaymentStatus.Paid,
            current.StagedPaymentSummary!.Status);
    }

    [Fact]
    public void STAGEDEBT03_Unjournaled_Completed_Receipt_Is_A_Blocker()
    {
        var (invoice, contract) = (Guid.NewGuid(), Guid.NewGuid());
        var payment = Receipt(invoice, contract, 1_000_000m);
        payment.Complete(DateTime.UtcNow);
        var result = Assess(invoice, contract,
            Array.Empty<InvoicePaymentAllocationJournalEntry>(),
            new[] { payment }, AfterDue);
        Assert.Contains("CompletedInvoicePaymentUnjournaled", result.Blockers);
        Assert.Null(result.StagedPaymentSummary);
    }

    [Fact]
    public void STAGEDEBT04_Cross_Invoice_Attribution_Is_Not_Mixed()
    {
        var (invoice, contract) = (Guid.NewGuid(), Guid.NewGuid());
        var other = Guid.NewGuid();
        var payment = Receipt(other, contract, 300_000m);
        payment.Complete(DateTime.UtcNow);
        var result = Assess(invoice, contract, new[] { Entry(payment, other) },
            new[] { payment }, AfterDue);
        Assert.True(result.HasCompleteStagedEvidence);
        Assert.Equal(1_000_000m,
            result.StagedPaymentSummary!.OutstandingBeforeAdjustments);
        Assert.Equal(IssuedInvoicePaymentStatus.Overdue,
            result.StagedPaymentSummary.Status);
    }

    [Fact]
    public void STAGEDEBT05_Foreign_Contract_Allocation_Is_Rejected()
    {
        var (invoice, contract) = (Guid.NewGuid(), Guid.NewGuid());
        var foreign = Guid.NewGuid();
        var payment = Receipt(invoice, foreign, 100_000m);
        payment.Complete(DateTime.UtcNow);
        var result = Assess(invoice, contract, new[] { Entry(payment, invoice) },
            new[] { payment }, Due);
        Assert.Contains("CrossContractAllocationInLedger", result.Blockers);
        Assert.Null(result.StagedPaymentSummary);
    }

    [Fact]
    public void STAGEDEBT06_Overpayment_Must_Be_Resolved_Outside_Preview()
    {
        var (invoice, contract) = (Guid.NewGuid(), Guid.NewGuid());
        var payment = Receipt(invoice, contract, 1_100_000m);
        payment.Complete(new DateTime(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc));
        var result = Assess(invoice, contract,
            new[] { Entry(payment, invoice) }, new[] { payment }, Due);
        Assert.Contains("UnresolvedInvoiceOverpayment", result.Blockers);
        Assert.Equal(100_000m,
            result.StagedPaymentSummary!.UnresolvedOverpayment);
        Assert.False(result.HasCompleteStagedEvidence);
        Assert.False(result.CanSettleDeposit);
    }

    [Fact]
    public void STAGEDEBT07_Duplicate_Journal_Refuses_Projection()
    {
        var (invoice, contract) = (Guid.NewGuid(), Guid.NewGuid());
        var payment = Receipt(invoice, contract, 100_000m);
        payment.Complete(DateTime.UtcNow);
        var journal = Entry(payment, invoice);
        var second = Entry(payment, invoice);
        var result = Assess(invoice, contract, new[] { journal, second },
            new[] { payment }, AfterDue);
        Assert.Contains("PaymentAlreadyUsedInAnotherAllocation", result.Blockers);
        Assert.Null(result.StagedPaymentSummary);
    }

    private static StagedIssuedInvoiceDebtReview Assess(
        Guid invoice, Guid contract,
        InvoicePaymentAllocationJournalEntry[] entries, Payment[] receipts,
        DateOnly asOf)
        => StagedIssuedInvoiceDebtEvaluator.Assess(
            invoice, contract, 1_000_000m, Due, asOf, entries, receipts);

    private static Payment Receipt(Guid invoice, Guid contract, decimal amount)
        => new(Guid.NewGuid(), "STAGE-" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid(), PaymentPurpose.InvoicePayment,
            PaymentMethod.BankTransfer, PaymentChannel.Manual, amount,
            contractId: contract, invoiceId: invoice);

    private static InvoicePaymentAllocationJournalEntry Entry(Payment payment, Guid invoice)
        => InvoicePaymentAllocationJournalEntry.StageForValidation(
            Guid.NewGuid(), payment, invoice,
            Guid.NewGuid(), DateTime.UtcNow);
}
