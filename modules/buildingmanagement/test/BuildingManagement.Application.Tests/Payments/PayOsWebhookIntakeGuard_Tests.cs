using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingManagement.Payments;
using Xunit;

namespace BuildingManagement.Payments.Tests;

public class PayOsWebhookIntakeGuard_Tests
{
    private const string Key = "isolated-only-checksum";

    private static PayOsPaymentIntent Intent(bool cancelled = false)
    {
        var intent = new PayOsPaymentIntent(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), 123, 3000, DateTime.UtcNow.AddHours(1),
            paymentId: Guid.NewGuid());
        intent.AttachProviderLink("LINK123");
        if (cancelled) intent.CancelUnpaid();
        return intent;
    }

    private static byte[] Signed(long amount = 3000, string reference = "BANKREF1")
    {
        var source = "amount=" + amount + "&code=00&currency=VND&orderCode=123" +
            "&paymentLinkId=LINK123&reference=" + reference;
        var sig = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Key), Encoding.UTF8.GetBytes(source)))
            .ToLowerInvariant();
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            code = "00", success = true,
            data = new
            {
                amount, code = "00", currency = "VND", orderCode = 123,
                paymentLinkId = "LINK123", reference
            },
            signature = sig
        }));
    }

    [Fact]
    public void Accepts_signed_matching_intent_without_posting_receipt()
    {
        var intent = Intent();
        var data = Signed();
        var parsed = PayOsWebhookIntakeGuard.Prepare(data, intent, Key);
        Assert.NotNull(parsed);
        Assert.Equal(intent.Id, parsed.PaymentIntentId);
        Assert.Equal(intent.BuildingId, parsed.BuildingId);
        Assert.Equal(intent.OrderCode, parsed.OrderCode);
        Assert.Equal("BANKREF1", parsed.ProviderReference);
        Assert.Equal(64, parsed.PayloadSha256.Length);
        Assert.False(parsed.NeedsLatePaymentReview);
        Assert.Equal(PayOsPaymentIntentStatus.AwaitingPayment, intent.Status);
    }

    [Fact]
    public void Cancelled_payment_funds_are_flagged_for_manual_review()
    {
        var parsed = PayOsWebhookIntakeGuard.Prepare(Signed(), Intent(cancelled: true), Key);
        Assert.NotNull(parsed);
        Assert.True(parsed.NeedsLatePaymentReview);
    }

    [Fact]
    public void Rejects_signed_but_wrong_amount_or_wrong_secret()
    {
        var intent = Intent();
        Assert.Null(PayOsWebhookIntakeGuard.Prepare(Signed(3001), intent, Key));
        Assert.Null(PayOsWebhookIntakeGuard.Prepare(Signed(), intent, "wrong-secret"));
        Assert.Null(PayOsWebhookIntakeGuard.Prepare(
            Signed(), new PayOsPaymentIntent(Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), 999, 3000, DateTime.UtcNow.AddHours(1),
                paymentId: Guid.NewGuid()), Key));
    }

    [Fact]
    public void Rejects_oversized_malformed_and_untrimmed_reference()
    {
        var intent = Intent();
        Assert.Null(PayOsWebhookIntakeGuard.Prepare(new byte[65537], intent, Key));
        Assert.Null(PayOsWebhookIntakeGuard.Prepare(
            Encoding.UTF8.GetBytes("{not-json}"), intent, Key));
        Assert.Null(PayOsWebhookIntakeGuard.Prepare(Signed(reference: " BANKREF1"), intent, Key));
        Assert.Null(PayOsWebhookIntakeGuard.Prepare(
            Signed(reference: new string('x', 129)), intent, Key));
    }
}
