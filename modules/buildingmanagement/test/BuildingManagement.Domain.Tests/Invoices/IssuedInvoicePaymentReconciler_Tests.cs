using System;
using BuildingManagement.Invoices;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using Xunit;

namespace BuildingManagement.Domain.Tests.Invoices;

public sealed class IssuedInvoicePaymentReconciler_Tests
{
    private static readonly DateOnly Due = new(2026, 11, 5);
    private static readonly DateOnly BeforeDue = new(2026, 11, 4);
    private static readonly DateOnly AfterDue = new(2026, 11, 6);

    [Fact]
    public void INVDEBT01_No_Payment_Is_Unpaid()
    {
        var (invoiceId, contractId) = Ids();
        var result = Assess(invoiceId, contractId, 1_000_000m,
            BeforeDue, Array.Empty<Payment>());

        Assert.Equal(IssuedInvoicePaymentStatus.Unpaid, result.Status);
        Assert.Equal(0m, result.CompletedPaymentsAmount);
        Assert.Equal(1_000_000m, result.OutstandingBeforeAdjustments);
        Assert.False(result.CanSettleDeposit);
    }

    [Fact]
    public void INVDEBT02_Completed_Partial_Payment_Is_Counted_Once()
    {
        var (invoiceId, contractId) = Ids();
        var payment = Create(invoiceId, contractId, 250_000m);
        payment.Complete(DateTime.UtcNow);
        var result = Assess(invoiceId, contractId, 1_000_000m,
            BeforeDue, new[] { payment });

        Assert.Equal(IssuedInvoicePaymentStatus.PartiallyPaid, result.Status);
        Assert.Equal(250_000m, result.CompletedPaymentsAmount);
        Assert.Equal(750_000m, result.OutstandingBeforeAdjustments);
        Assert.Equal(1, result.CompletedPaymentCount);
    }

    [Fact]
    public void INVDEBT03_Multiple_Unique_Completed_Receipts_Fully_Pay()
    {
        var (invoiceId, contractId) = Ids();
        var first = Create(invoiceId, contractId, 300_000m);
        var second = Create(invoiceId, contractId, 700_000m);
        first.Complete(DateTime.UtcNow, "FIRST");
        second.Complete(DateTime.UtcNow, "SECOND");

        var result = Assess(invoiceId, contractId, 1_000_000m,
            AfterDue, new[] { first, second });

        Assert.Equal(IssuedInvoicePaymentStatus.Paid, result.Status);
        Assert.Equal(1_000_000m, result.CompletedPaymentsAmount);
        Assert.Equal(0m, result.OutstandingBeforeAdjustments);
        Assert.Equal(2, result.CompletedPaymentCount);
    }

    [Fact]
    public void INVDEBT04_Overdue_Wins_Over_Partial_When_Balance_Remains()
    {
        var (invoiceId, contractId) = Ids();
        var payment = Create(invoiceId, contractId, 10_000m);
        payment.Complete(DateTime.UtcNow);

        var result = Assess(invoiceId, contractId, 1_000_000m,
            AfterDue, new[] { payment });
        Assert.Equal(IssuedInvoicePaymentStatus.Overdue, result.Status);
        Assert.Equal(990_000m, result.OutstandingBeforeAdjustments);
    }

    [Fact]
    public void INVDEBT05_Pending_Failed_And_Cancelled_Are_Not_Receipts()
    {
        var (invoiceId, contractId) = Ids();
        var pending = Create(invoiceId, contractId, 200_000m);
        var failed = Create(invoiceId, contractId, 300_000m);
        var cancelled = Create(invoiceId, contractId, 500_000m);
        failed.Fail();
        cancelled.Cancel();

        var result = Assess(invoiceId, contractId, 1_000_000m,
            BeforeDue, new[] { pending, failed, cancelled });

        Assert.Equal(IssuedInvoicePaymentStatus.Unpaid, result.Status);
        Assert.Equal(0m, result.CompletedPaymentsAmount);
        Assert.Equal(1_000_000m, result.OutstandingBeforeAdjustments);
    }

    [Fact]
    public void INVDEBT06_Rejects_Same_Payment_Id_Twice()
    {
        var (invoiceId, contractId) = Ids();
        var payment = Create(invoiceId, contractId, 500_000m);
        payment.Complete(DateTime.UtcNow);
        Assert.Throws<InvalidOperationException>(() =>
            Assess(invoiceId, contractId, 1_000_000m,
                BeforeDue, new[] { payment, payment }));
    }

    [Fact]
    public void INVDEBT07_Rejects_Duplicate_Bank_Transaction_Reference()
    {
        var (invoiceId, contractId) = Ids();
        var first = Create(invoiceId, contractId, 100_000m);
        var second = Create(invoiceId, contractId, 100_000m);
        first.Complete(DateTime.UtcNow, "TRACE-123");
        second.Complete(DateTime.UtcNow, "trace-123");

        Assert.Throws<InvalidOperationException>(() =>
            Assess(invoiceId, contractId, 1_000_000m,
                BeforeDue, new[] { first, second }));
    }

    [Fact]
    public void INVDEBT08_Deposit_Cannot_Pay_Invoice()
    {
        var (invoiceId, contractId) = Ids();
        var deposit = Create(invoiceId, contractId, 1_000_000m,
            PaymentPurpose.ContractDeposit);
        deposit.Complete(DateTime.UtcNow);

        Assert.Throws<InvalidOperationException>(() =>
            Assess(invoiceId, contractId, 1_000_000m,
                BeforeDue, new[] { deposit }));
    }

    [Fact]
    public void INVDEBT09_Cross_Invoice_And_Cross_Contract_Payments_Rejected()
    {
        var (invoiceId, contractId) = Ids();
        var otherInvoice = Create(Guid.NewGuid(), contractId, 100_000m);
        var otherContract = Create(invoiceId, Guid.NewGuid(), 100_000m);
        otherInvoice.Complete(DateTime.UtcNow);
        otherContract.Complete(DateTime.UtcNow);
        Assert.Throws<InvalidOperationException>(() =>
            Assess(invoiceId, contractId, 1_000_000m,
                BeforeDue, new[] { otherInvoice }));
        Assert.Throws<InvalidOperationException>(() =>
            Assess(invoiceId, contractId, 1_000_000m,
                BeforeDue, new[] { otherContract }));
    }

    [Fact]
    public void INVDEBT10_Overpayment_Is_Not_Negative_Debt_Or_Deposit_Credit()
    {
        var (invoiceId, contractId) = Ids();
        var payment = Create(invoiceId, contractId, 1_100_000m);
        payment.Complete(DateTime.UtcNow);

        var result = Assess(invoiceId, contractId, 1_000_000m,
            BeforeDue, new[] { payment });

        Assert.Equal(IssuedInvoicePaymentStatus.Paid, result.Status);
        Assert.Equal(0m, result.OutstandingBeforeAdjustments);
        Assert.Equal(100_000m, result.UnresolvedOverpayment);
        Assert.True(result.HasUnresolvedOverpayment);
        Assert.False(result.CanSettleDeposit);
    }

    [Fact]
    public void INVDEBT11_Due_Today_Is_Not_Yet_Overdue()
    {
        var (invoiceId, contractId) = Ids();
        Assert.Equal(IssuedInvoicePaymentStatus.Unpaid,
            Assess(invoiceId, contractId, 100m, Due,
                Array.Empty<Payment>()).Status);
    }

    [Fact]
    public void INVDEBT12_Future_Receipt_Does_Not_Reduce_Historical_Debt()
    {
        var (invoiceId, contractId) = Ids();
        var payment = Create(invoiceId, contractId, 1_000_000m);
        payment.Complete(new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc));

        var historical = Assess(invoiceId, contractId, 1_000_000m,
            BeforeDue, new[] { payment });
        Assert.Equal(IssuedInvoicePaymentStatus.Unpaid, historical.Status);
        Assert.Equal(1_000_000m, historical.OutstandingBeforeAdjustments);
        Assert.Equal(0, historical.CompletedPaymentCount);

        var current = Assess(invoiceId, contractId, 1_000_000m,
            Due, new[] { payment });
        Assert.Equal(IssuedInvoicePaymentStatus.Paid, current.Status);
        Assert.Equal(0m, current.OutstandingBeforeAdjustments);
        Assert.Equal(1, current.CompletedPaymentCount);
    }

    [Fact]
    public void INVDEBT13_Vietnam_Calendar_Determines_AsOf_Cutoff()
    {
        var (invoiceId, contractId) = Ids();
        // 16:59 UTC = 23:59 on 04/11 in Vietnam.
        var beforeMidnight = Create(invoiceId, contractId, 200_000m);
        beforeMidnight.Complete(
            new DateTime(2026, 11, 4, 16, 59, 0, DateTimeKind.Utc));
        // 18:00 UTC is already 01:00 on 05/11 in Vietnam.
        var afterMidnight = Create(invoiceId, contractId, 300_000m);
        afterMidnight.Complete(
            new DateTime(2026, 11, 4, 18, 0, 0, DateTimeKind.Utc));

        var historical = Assess(invoiceId, contractId, 1_000_000m,
            BeforeDue, new[] { beforeMidnight, afterMidnight });
        Assert.Equal(IssuedInvoicePaymentStatus.PartiallyPaid, historical.Status);
        Assert.Equal(200_000m, historical.CompletedPaymentsAmount);
        Assert.Equal(800_000m, historical.OutstandingBeforeAdjustments);
        Assert.Equal(1, historical.CompletedPaymentCount);
    }

    [Fact]
    public void INVDEBT14_NonUtc_Completed_Receipt_Cannot_Be_DateClassified()
    {
        var (invoiceId, contractId) = Ids();
        var payment = Create(invoiceId, contractId, 100_000m);
        payment.Complete(
            new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Unspecified));

        Assert.Throws<InvalidOperationException>(() =>
            Assess(invoiceId, contractId, 1_000_000m,
                BeforeDue, new[] { payment }));
    }

    [Fact]
    public void INVDEBT15_Future_Receipt_Does_Not_Clear_Overdue_Balance()
    {
        var (invoiceId, contractId) = Ids();
        var payment = Create(invoiceId, contractId, 1_000_000m);
        payment.Complete(new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc));

        var overdue = Assess(invoiceId, contractId, 1_000_000m,
            AfterDue, new[] { payment });
        Assert.Equal(IssuedInvoicePaymentStatus.Overdue, overdue.Status);
        Assert.Equal(1_000_000m, overdue.OutstandingBeforeAdjustments);

        var paid = Assess(invoiceId, contractId, 1_000_000m,
            new DateOnly(2026, 11, 8), new[] { payment });
        Assert.Equal(IssuedInvoicePaymentStatus.Paid, paid.Status);
        Assert.Equal(0m, paid.OutstandingBeforeAdjustments);
    }

    private static IssuedInvoicePaymentSummary Assess(
        Guid invoiceId, Guid contractId, decimal amount,
        DateOnly asOfDate, Payment[] payments)
        => IssuedInvoicePaymentReconciler.Assess(
            invoiceId, contractId, amount, Due, asOfDate, payments);

    private static (Guid InvoiceId, Guid ContractId) Ids()
        => (Guid.NewGuid(), Guid.NewGuid());

    private static Payment Create(
        Guid invoiceId, Guid contractId, decimal amount,
        PaymentPurpose purpose = PaymentPurpose.InvoicePayment)
        => new Payment(
            Guid.NewGuid(),
            "DEBT-" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid(),
            purpose,
            PaymentMethod.BankTransfer,
            PaymentChannel.Manual,
            amount,
            contractId: contractId,
            invoiceId: invoiceId);
}
