using System;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using Xunit;

namespace BuildingManagement.Domain.Tests.Payments;

public sealed class PaymentTerminalState_Tests
{
    [Fact]
    public void PAYSTATE01_Pending_Invoice_Receipt_Can_Complete_Exactly_Once()
    {
        var payment = NewPayment(PaymentPurpose.InvoicePayment);
        var settledAt = new DateTime(2026, 10, 27, 7, 0, 0, DateTimeKind.Utc);

        payment.Complete(settledAt, "FIRST-SETTLEMENT");
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(settledAt, payment.PaidAt);
        Assert.Equal("FIRST-SETTLEMENT", payment.TransactionReference);
        Assert.Null(payment.DepositDisposition);

        Assert.Throws<InvalidOperationException>(() =>
            payment.Complete(settledAt.AddMinutes(1), "DUPLICATE"));
        Assert.Equal(settledAt, payment.PaidAt);
        Assert.Equal("FIRST-SETTLEMENT", payment.TransactionReference);
    }

    [Fact]
    public void PAYSTATE02_Completed_Payment_Cannot_Be_Cancelled_Or_Failed()
    {
        var payment = NewPayment(PaymentPurpose.InvoicePayment);
        payment.Complete(DateTime.UtcNow);
        var paidAt = payment.PaidAt;

        Assert.Throws<InvalidOperationException>(payment.Cancel);
        Assert.Throws<InvalidOperationException>(payment.Fail);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(paidAt, payment.PaidAt);
    }

    [Fact]
    public void PAYSTATE03_Cancelled_Payment_Cannot_Be_Completed_Or_Failed()
    {
        var payment = NewPayment(PaymentPurpose.InvoicePayment);
        payment.Cancel();

        Assert.Throws<InvalidOperationException>(() =>
            payment.Complete(DateTime.UtcNow));
        Assert.Throws<InvalidOperationException>(payment.Fail);
        Assert.Throws<InvalidOperationException>(payment.Cancel);
        Assert.Equal(PaymentStatus.Cancelled, payment.Status);
        Assert.Null(payment.PaidAt);
    }

    [Fact]
    public void PAYSTATE04_Failed_Payment_Cannot_Be_Completed_Or_Cancelled()
    {
        var payment = NewPayment(PaymentPurpose.InvoicePayment);
        payment.Fail();

        Assert.Throws<InvalidOperationException>(() =>
            payment.Complete(DateTime.UtcNow));
        Assert.Throws<InvalidOperationException>(payment.Cancel);
        Assert.Throws<InvalidOperationException>(payment.Fail);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Null(payment.PaidAt);
    }

    [Fact]
    public void PAYSTATE05_Invalid_Completion_Time_Does_Not_Mutate()
    {
        var payment = NewPayment(PaymentPurpose.InvoicePayment);
        Assert.Throws<ArgumentException>(() =>
            payment.Complete(default, "SHOULD-NOT-BE-SET"));

        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Null(payment.PaidAt);
        Assert.Null(payment.TransactionReference);
    }

    [Theory]
    [InlineData(PaymentPurpose.ReservationDeposit)]
    [InlineData(PaymentPurpose.ContractDeposit)]
    public void PAYSTATE06_Completed_Deposit_Retains_Unapplied_Disposition(
        PaymentPurpose purpose)
    {
        var payment = NewPayment(purpose);
        payment.Complete(DateTime.UtcNow);

        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(DepositDisposition.Unapplied, payment.DepositDisposition);
        Assert.Throws<InvalidOperationException>(payment.Cancel);
        Assert.Equal(DepositDisposition.Unapplied, payment.DepositDisposition);
    }

    private static Payment NewPayment(PaymentPurpose purpose)
        => new Payment(
            Guid.NewGuid(),
            "PAYSTATE-" + Guid.NewGuid().ToString("N"),
            Guid.NewGuid(),
            purpose,
            PaymentMethod.Cash,
            PaymentChannel.Manual,
            500000m);
}
