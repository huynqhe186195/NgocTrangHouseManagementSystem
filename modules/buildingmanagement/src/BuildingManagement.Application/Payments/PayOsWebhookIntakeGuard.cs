using System;
using System.Security.Cryptography;
using System.Text.Json;

namespace BuildingManagement.Payments;

/// <summary>
/// Parses a signed payOS notification into a bounded, intent-matched event.
/// There is NO financial posting, external status polling or acknowledgement here.
/// A persistence writer must independently re-check current intent state under
/// a database lock, and reconcile provider status before making any journal entry.
/// </summary>
public static class PayOsWebhookIntakeGuard
{
    public const int MaximumBodyBytes = 65536;

    public static PayOsVerifiedWebhook? Prepare(
        ReadOnlyMemory<byte> rawBody, PayOsPaymentIntent intent, string checksumKey)
    {
        if (intent is null || rawBody.IsEmpty || rawBody.Length > MaximumBodyBytes ||
            string.IsNullOrWhiteSpace(checksumKey) ||
            intent.Id == Guid.Empty || intent.BuildingId == Guid.Empty ||
            intent.Status == PayOsPaymentIntentStatus.Created ||
            string.IsNullOrWhiteSpace(intent.PaymentLinkId))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            if (!PayOsPaymentConfirmationGuard.Matches(doc.RootElement,
                intent.OrderCode, intent.AmountVnd, intent.PaymentLinkId!, checksumKey))
                return null;

            var data = doc.RootElement.GetProperty("data");
            var reference = data.GetProperty("reference").GetString();
            if (string.IsNullOrWhiteSpace(reference) ||
                reference.Length > 128 || reference != reference.Trim())
                return null;

            var digest = Convert.ToHexString(SHA256.HashData(rawBody.Span)).ToLowerInvariant();
            return new PayOsVerifiedWebhook(
                intent.Id, intent.BuildingId, intent.OrderCode,
                reference, digest,
                intent.Status == PayOsPaymentIntentStatus.Cancelled);
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}

/// <summary>
/// Only PayOsWebhookIntakeGuard can construct a verified event. Values must be
/// checked against the locked database row again before they are persisted.
/// </summary>
public sealed class PayOsVerifiedWebhook
{
    public Guid PaymentIntentId { get; }
    public Guid BuildingId { get; }
    public long OrderCode { get; }
    public string ProviderReference { get; }
    public string PayloadSha256 { get; }
    public bool NeedsLatePaymentReview { get; }

    internal PayOsVerifiedWebhook(Guid paymentIntentId, Guid buildingId,
        long orderCode, string providerReference, string payloadSha256,
        bool needsLatePaymentReview)
    {
        PaymentIntentId = paymentIntentId;
        BuildingId = buildingId;
        OrderCode = orderCode;
        ProviderReference = providerReference;
        PayloadSha256 = payloadSha256;
        NeedsLatePaymentReview = needsLatePaymentReview;
    }
}
