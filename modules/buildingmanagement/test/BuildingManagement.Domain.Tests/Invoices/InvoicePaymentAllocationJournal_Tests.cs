using System;
using BuildingManagement.Invoices;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using Xunit;

namespace BuildingManagement.Domain.Tests.Invoices;

public sealed class InvoicePaymentAllocationJournal_Tests
{
    [Fact]
    public void ALLOCJOURNAL01_Completed_Receipt_Has_Verifiable_Staged_Entry_But_No_Debt()
    {
        var (payment, invoiceId, contractId) = Receipt();
        var entry = Stage(payment, invoiceId);

        entry.VerifySourceReceipt(payment);
        Assert.Equal(payment.Id, entry.PaymentId);
        Assert.Equal(contractId, entry.ContractId);
        Assert.Equal(payment.Amount, entry.Amount);
        var audit = InvoicePaymentAllocationJournalAuditor.Assess(
            new[] { entry }, new[] { payment });
        Assert.Equal(1, audit.VerifiedStagedEntryCount);
        Assert.Equal(payment.Amount, audit.VerifiedStagedAmount);
        Assert.Contains("IssuedInvoiceLedgerAndAllocationAuthorizationMissing",
            audit.Blockers);
        Assert.False(entry.IsRecognizedAllocation);
        Assert.False(entry.CanReduceOutstandingDebt);
        Assert.False(entry.CanOffsetDeposit);
        Assert.False(audit.CanPost);
        Assert.False(audit.CanReduceDebt);
    }

    [Fact]
    public void ALLOCJOURNAL02_Pending_Failed_And_Cancelled_Payments_Cannot_Be_Staged()
    {
        foreach (var status in new[] { PaymentStatus.Pending,
                     PaymentStatus.Failed, PaymentStatus.Cancelled })
        {
            var contractId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var payment = NewReceipt(invoiceId, contractId);
            if (status == PaymentStatus.Failed) payment.Fail();
            if (status == PaymentStatus.Cancelled) payment.Cancel();
            Assert.Throws<InvalidOperationException>(
                () => Stage(payment, invoiceId));
        }
    }

    [Fact]
    public void ALLOCJOURNAL03_Deposits_Never_Apply_To_Invoice_Debt()
    {
        var contractId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var deposit = NewReceipt(
            invoiceId, contractId, PaymentPurpose.ContractDeposit);
        deposit.Complete(DateTime.UtcNow);

        Assert.Throws<InvalidOperationException>(
            () => Stage(deposit, invoiceId));
    }

    [Fact]
    public void ALLOCJOURNAL04_Other_Invoice_Or_No_Contract_Is_Rejected()
    {
        var (payment, invoiceId, _) = Receipt();
        Assert.Throws<InvalidOperationException>(
            () => Stage(payment, Guid.NewGuid()));

        var missingContract = NewReceipt(
            invoiceId, contractId: null);
        missingContract.Complete(DateTime.UtcNow);
        Assert.Throws<InvalidOperationException>(
            () => Stage(missingContract, invoiceId));
    }

    [Fact]
    public void ALLOCJOURNAL05_Invalid_Actor_Id_Or_Utc_Time_Is_Rejected()
    {
        var (payment, invoiceId, _) = Receipt();
        Assert.Throws<ArgumentException>(() =>
            InvoicePaymentAllocationJournalEntry.StageForValidation(
                Guid.NewGuid(), payment, invoiceId, Guid.Empty, DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() =>
            InvoicePaymentAllocationJournalEntry.StageForValidation(
                Guid.NewGuid(), payment, invoiceId, Guid.NewGuid(),
                DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void ALLOCJOURNAL06_Receipt_Amount_Mutation_Is_Detected()
    {
        var (payment, invoiceId, _) = Receipt();
        var entry = Stage(payment, invoiceId);
        typeof(Payment).GetProperty(nameof(Payment.Amount))!
            .SetValue(payment, payment.Amount + 1m);

        Assert.Throws<InvalidOperationException>(
            () => entry.VerifySourceReceipt(payment));
        var audit = InvoicePaymentAllocationJournalAuditor.Assess(
            new[] { entry }, new[] { payment });
        Assert.Contains("AllocationSourceReceiptMismatch", audit.Blockers);
        Assert.Null(audit.VerifiedStagedAmount);
    }

    [Fact]
    public void ALLOCJOURNAL07_One_Payment_Cannot_Appear_In_Two_Journal_Entries()
    {
        var (payment, invoiceId, _) = Receipt();
        var first = Stage(payment, invoiceId);
        var second = Stage(payment, invoiceId);

        var audit = InvoicePaymentAllocationJournalAuditor.Assess(
            new[] { first, second }, new[] { payment });
        Assert.Contains("PaymentAlreadyUsedInAnotherAllocation", audit.Blockers);
        Assert.Null(audit.VerifiedStagedAmount);
        Assert.False(audit.CanPost);
    }

    [Fact]
    public void ALLOCJOURNAL08_Duplicate_Transaction_References_Are_Flagged()
    {
        var invoiceId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var first = NewReceipt(invoiceId, contractId);
        var second = NewReceipt(invoiceId, contractId);
        first.Complete(DateTime.UtcNow, "TRACE-ALLOC-1");
        second.Complete(DateTime.UtcNow, " trace-alloc-1 ");

        var audit = InvoicePaymentAllocationJournalAuditor.Assess(
            new[] { Stage(first, invoiceId), Stage(second, invoiceId) },
            new[] { first, second });

        Assert.Contains("DuplicateAllocationTransactionReference", audit.Blockers);
        Assert.False(audit.CanReduceDebt);
    }

    [Fact]
    public void ALLOCJOURNAL09_Missing_Receipt_Denies_Allocation()
    {
        var (payment, invoiceId, _) = Receipt();
        var audit = InvoicePaymentAllocationJournalAuditor.Assess(
            new[] { Stage(payment, invoiceId) }, Array.Empty<Payment>());

        Assert.Contains("AllocationSourcePaymentMissing", audit.Blockers);
        Assert.Null(audit.VerifiedStagedAmount);
    }

    [Fact]
    public void ALLOCJOURNAL10_Fractional_Vnd_Is_Not_Eligible_For_Staging()
    {
        var contractId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var payment = NewReceipt(invoiceId, contractId, amount: 1000.5m);
        payment.Complete(DateTime.UtcNow);
        Assert.Throws<InvalidOperationException>(() => Stage(payment, invoiceId));
    }

    [Fact]
    public void ALLOCJOURNAL11_Modified_Payment_Date_And_Reference_Are_Detected()
    {
        var (payment, invoiceId, _) = Receipt();
        var entry = Stage(payment, invoiceId);
        typeof(Payment).GetProperty(nameof(Payment.PaidAt))!
            .SetValue(payment, DateTime.UtcNow.AddDays(1));

        Assert.Throws<InvalidOperationException>(
            () => entry.VerifySourceReceipt(payment));
    }

    [Fact]
    public void ALLOCJOURNAL12_Contract_Scope_Audit_Finds_Unjournaled_Completed_Receipt()
    {
        var (payment, _, contractId) = Receipt();
        var audit = InvoicePaymentAllocationJournalAuditor.Assess(
            Array.Empty<InvoicePaymentAllocationJournalEntry>(),
            new[] { payment }, contractId);

        Assert.Contains("CompletedInvoicePaymentUnjournaled", audit.Blockers);
        Assert.Null(audit.VerifiedStagedAmount);
        Assert.False(audit.CanReduceDebt);
    }

    [Fact]
    public void ALLOCJOURNAL13_Contract_Scope_Audit_Rejects_Entry_From_Other_Contract()
    {
        var (payment, invoiceId, contractId) = Receipt();
        var journal = Stage(payment, invoiceId);
        typeof(InvoicePaymentAllocationJournalEntry)
            .GetProperty(nameof(InvoicePaymentAllocationJournalEntry.ContractId))!
            .SetValue(journal, Guid.NewGuid());

        var audit = InvoicePaymentAllocationJournalAuditor.Assess(
            new[] { journal }, new[] { payment }, contractId);

        Assert.Contains("CrossContractAllocationInLedger", audit.Blockers);
        Assert.Contains("AllocationSourceReceiptMismatch", audit.Blockers);
        Assert.Null(audit.VerifiedStagedAmount);
    }

    [Fact]
    public void ALLOCJOURNAL14_Completed_Invoice_Receipt_Without_Invoice_Id_Is_Reported()
    {
        var contractId = Guid.NewGuid();
        var payment = new Payment(
            Guid.NewGuid(), "UNATTR-" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid(), PaymentPurpose.InvoicePayment,
            PaymentMethod.BankTransfer, PaymentChannel.Manual, 125000m,
            contractId: contractId);
        payment.Complete(DateTime.UtcNow);

        var audit = InvoicePaymentAllocationJournalAuditor.Assess(
            Array.Empty<InvoicePaymentAllocationJournalEntry>(),
            new[] { payment }, contractId);

        Assert.Contains("CompletedInvoicePaymentUnattributed", audit.Blockers);
        Assert.Contains("CompletedInvoicePaymentUnjournaled", audit.Blockers);
    }

    [Fact]
    public void ALLOCJOURNAL15_Pending_Unjournaled_Receipt_Is_Not_Recognized()
    {
        var invoiceId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var pending = NewReceipt(invoiceId, contractId);

        var audit = InvoicePaymentAllocationJournalAuditor.Assess(
            Array.Empty<InvoicePaymentAllocationJournalEntry>(),
            new[] { pending }, contractId);

        Assert.DoesNotContain("CompletedInvoicePaymentUnjournaled", audit.Blockers);
        Assert.Equal(0m, audit.VerifiedStagedAmount);
        Assert.False(audit.CanReduceDebt);
    }

    [Fact]
    public void ALLOCJOURNAL16_Valid_Staged_Payment_Matches_Scope_Without_Posting()
    {
        var (payment, invoiceId, contractId) = Receipt();
        var audit = InvoicePaymentAllocationJournalAuditor.Assess(
            new[] { Stage(payment, invoiceId) }, new[] { payment }, contractId);

        Assert.DoesNotContain("CompletedInvoicePaymentUnjournaled", audit.Blockers);
        Assert.DoesNotContain("CrossContractAllocationInLedger", audit.Blockers);
        Assert.Equal(payment.Amount, audit.VerifiedStagedAmount);
        Assert.False(audit.CanPost);
    }

    [Fact]
    public void ALLOCJOURNAL17_Empty_Contract_Id_Is_Not_A_Valid_Scope()
    {
        Assert.Throws<ArgumentException>(() =>
            InvoicePaymentAllocationJournalAuditor.Assess(
                Array.Empty<InvoicePaymentAllocationJournalEntry>(),
                Array.Empty<Payment>(), Guid.Empty));
    }

    private static (Payment Payment, Guid InvoiceId, Guid ContractId) Receipt()
    {
        var invoiceId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var payment = NewReceipt(invoiceId, contractId);
        payment.Complete(DateTime.UtcNow, "TRACE-" + Guid.NewGuid().ToString("N"));
        return (payment, invoiceId, contractId);
    }

    private static Payment NewReceipt(
        Guid invoiceId, Guid? contractId,
        PaymentPurpose purpose = PaymentPurpose.InvoicePayment,
        decimal amount = 250_000m)
        => new(
            Guid.NewGuid(),
            "ALLOC-" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid(), purpose, PaymentMethod.BankTransfer,
            PaymentChannel.Manual, amount,
            contractId: contractId, invoiceId: invoiceId);

    private static InvoicePaymentAllocationJournalEntry Stage(
        Payment payment, Guid invoiceId)
        => InvoicePaymentAllocationJournalEntry.StageForValidation(
            Guid.NewGuid(), payment, invoiceId,
            Guid.NewGuid(), DateTime.UtcNow);
}
