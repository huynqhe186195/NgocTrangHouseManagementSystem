using System;
using BuildingManagement.Payments;
using Xunit;

namespace BuildingManagement.Payments.Tests;

public class PayOsPaymentReconciliationPolicy_Tests
{
    private static PayOsPaymentIntent Intent(bool cancel = false, bool post = false)
    {
        var intent = new PayOsPaymentIntent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 123, 150000,
            DateTime.UtcNow.AddHours(1), paymentId: Guid.NewGuid());
        intent.AttachProviderLink("LINK123");
        if (post)
        {
            intent.MarkConfirmed("REF", DateTime.UtcNow);
            intent.MarkPosted();
        }
        if (cancel) intent.CancelUnpaid();
        return intent;
    }

    private static PayOsPaymentLinkSnapshot Snapshot(
        string status = "PAID", long paid = 150000, long remaining = 0,
        string link = "LINK123", long amount = 150000) =>
        new(123, amount, paid, remaining, link, status);

    [Fact]
    public void Paid_provider_link_only_flags_funds_pending_independent_posting()
    {
        Assert.Equal(PayOsReconciliationDisposition.FundsDetectedPendingIndependentPosting,
            PayOsPaymentReconciliationPolicy.Classify(Intent(), Snapshot()));
        Assert.Equal(PayOsReconciliationDisposition.AlreadyPosted,
            PayOsPaymentReconciliationPolicy.Classify(Intent(post: true), Snapshot()));
    }

    [Fact]
    public void Cancelled_checkout_with_late_funds_requires_review_not_loss()
    {
        Assert.Equal(PayOsReconciliationDisposition.LateFundsRequireReview,
            PayOsPaymentReconciliationPolicy.Classify(Intent(cancel: true), Snapshot()));
        Assert.Equal(PayOsReconciliationDisposition.NoFundsConfirmed,
            PayOsPaymentReconciliationPolicy.Classify(Intent(cancel: true),
                Snapshot("CANCELLED", 0, 150000)));
    }

    [Theory]
    [InlineData(149999, 1, "PAID")]
    [InlineData(100000, 50000, "PROCESSING")]
    [InlineData(150001, 0, "PAID")]
    [InlineData(150000, 1, "PAID")]
    public void Partial_or_inconsistent_paid_amount_is_never_automatically_posted(
        long paid, long remaining, string status)
    {
        Assert.Equal(PayOsReconciliationDisposition.ConflictRequiresReview,
            PayOsPaymentReconciliationPolicy.Classify(Intent(),
                Snapshot(status, paid, remaining)));
    }

    [Fact]
    public void Wrong_link_and_amount_raise_conflict()
    {
        Assert.Equal(PayOsReconciliationDisposition.ConflictRequiresReview,
            PayOsPaymentReconciliationPolicy.Classify(Intent(), Snapshot(link: "OTHER")));
        Assert.Equal(PayOsReconciliationDisposition.ConflictRequiresReview,
            PayOsPaymentReconciliationPolicy.Classify(Intent(), Snapshot(amount: 200000)));
    }

    [Fact]
    public void An_unpaid_pending_checkout_is_not_a_receipt()
    {
        Assert.Equal(PayOsReconciliationDisposition.AwaitingPayment,
            PayOsPaymentReconciliationPolicy.Classify(Intent(),
                Snapshot("PENDING", 0, 150000)));
    }
}
