using System;
using BuildingManagement.Payments;
using Xunit;

namespace BuildingManagement.Payments.Tests;

public class PayOsWebhookInboxRecord_Tests
{
    private static PayOsWebhookInboxRecord NewInbox() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 123456,
            "BANK-REF-001", new string('a', 64), DateTime.UtcNow);

    [Fact]
    public void New_signed_event_starts_pending_and_does_not_post_money()
    {
        var inbox = NewInbox();
        Assert.Equal(PayOsWebhookInboxStatus.PendingReconciliation, inbox.Status);
        Assert.Null(inbox.ReviewedAtUtc);
        Assert.Equal("BANK-REF-001", inbox.ProviderReference);
    }

    [Fact]
    public void Reconciled_event_cannot_be_reviewed_twice()
    {
        var inbox = NewInbox();
        inbox.MarkReconciled(DateTime.UtcNow);
        Assert.Equal(PayOsWebhookInboxStatus.ReconciledNoPosting, inbox.Status);
        Assert.Throws<InvalidOperationException>(() => inbox.MarkReconciled(DateTime.UtcNow));
        Assert.Throws<InvalidOperationException>(() => inbox.RequireManualReview(DateTime.UtcNow));
    }

    [Fact]
    public void Manual_review_is_terminal_in_this_source_only_model()
    {
        var inbox = NewInbox();
        inbox.RequireManualReview(DateTime.UtcNow);
        Assert.Equal(PayOsWebhookInboxStatus.RequiresManualReview, inbox.Status);
        Assert.Throws<InvalidOperationException>(() => inbox.MarkReconciled(DateTime.UtcNow));
    }

    [Fact]
    public void Refuses_missing_identity_malformed_digest_or_non_utc_receipt()
    {
        var building = Guid.NewGuid();
        var intent = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() =>
            new PayOsWebhookInboxRecord(Guid.Empty, building, intent, 123,
                "REF", new string('a', 64), DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() =>
            new PayOsWebhookInboxRecord(Guid.NewGuid(), building, intent, 123,
                "REF", new string('G', 64), DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() =>
            new PayOsWebhookInboxRecord(Guid.NewGuid(), building, intent, 123,
                "REF", new string('a', 64), DateTime.Now));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PayOsWebhookInboxRecord(Guid.NewGuid(), building, intent, 0,
                "REF", new string('a', 64), DateTime.UtcNow));
    }
}
