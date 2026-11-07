using System;
using BuildingManagement.Payments;
using Xunit;

namespace BuildingManagement.Payments.Tests;

public class PayOsPaymentIntent_Tests
{
    private static PayOsPaymentIntent NewIntent() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            123456, 150000, DateTime.UtcNow.AddDays(1),
            paymentId: Guid.NewGuid());

    [Fact]
    public void New_intent_is_unpaid_and_cannot_post_or_confirm_without_link()
    {
        var intent = NewIntent();
        Assert.Equal(PayOsPaymentIntentStatus.Created, intent.Status);
        Assert.Null(intent.ConfirmedAtUtc);
        Assert.Throws<InvalidOperationException>(() =>
            intent.MarkConfirmed("TX-1", DateTime.UtcNow));
        Assert.Throws<InvalidOperationException>(() => intent.MarkPosted());
    }

    [Fact]
    public void Link_confirmation_posting_is_single_direction()
    {
        var intent = NewIntent();
        intent.AttachProviderLink("LINK-1");
        Assert.Equal(PayOsPaymentIntentStatus.AwaitingPayment, intent.Status);
        Assert.Throws<InvalidOperationException>(() => intent.AttachProviderLink("LINK-2"));
        intent.MarkConfirmed("REF-1", DateTime.UtcNow);
        Assert.Equal(PayOsPaymentIntentStatus.ConfirmedPendingPosting, intent.Status);
        Assert.Throws<InvalidOperationException>(() =>
            intent.MarkConfirmed("REF-1", DateTime.UtcNow));
        Assert.Throws<InvalidOperationException>(() => intent.CancelUnpaid());
        intent.MarkPosted();
        Assert.Equal(PayOsPaymentIntentStatus.Posted, intent.Status);
        Assert.Throws<InvalidOperationException>(() => intent.MarkPosted());
    }

    [Fact]
    public void Cancelling_unpaid_intent_never_creates_a_receipt()
    {
        var intent = NewIntent();
        intent.CancelUnpaid();
        Assert.Equal(PayOsPaymentIntentStatus.Cancelled, intent.Status);
        Assert.Null(intent.ProviderReference);
        Assert.Throws<InvalidOperationException>(() => intent.MarkPosted());
        Assert.Throws<InvalidOperationException>(() => intent.AttachProviderLink("LINK-1"));
    }

    [Fact]
    public void Requires_exactly_one_target_and_positive_vnd()
    {
        var payer = Guid.NewGuid();
        var building = Guid.NewGuid();
        var expires = DateTime.UtcNow.AddHours(1);
        Assert.Throws<ArgumentException>(() =>
            new PayOsPaymentIntent(Guid.NewGuid(), building, payer, 123, 100,
                expires));
        Assert.Throws<ArgumentException>(() =>
            new PayOsPaymentIntent(Guid.NewGuid(), building, payer, 123, 100,
                expires, Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PayOsPaymentIntent(Guid.NewGuid(), building, payer, 123, 0,
                expires, paymentId: Guid.NewGuid()));
    }
}
