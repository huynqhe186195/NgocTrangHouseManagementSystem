using System;
using System.Text.Json;

namespace BuildingManagement.Payments;

/// <summary>
/// Read-only confirmation guard for a previously persisted payOS intent.
/// Must be used AFTER signature validation and BEFORE any financial posting.
/// The caller must still lock/deduplicate/reconcile the intent in the database.
/// </summary>
public static class PayOsPaymentConfirmationGuard
{
    public static bool Matches(
        JsonElement webhook,
        long expectedOrderCode,
        long expectedAmountVnd,
        string expectedPaymentLinkId,
        string checksumKey)
    {
        if (expectedOrderCode <= 0 || expectedAmountVnd <= 0 ||
            string.IsNullOrWhiteSpace(expectedPaymentLinkId) ||
            !PayOsPaymentWebhookSignature.IsValid(webhook, checksumKey))
            return false;

        if (!webhook.TryGetProperty("success", out var success) ||
            success.ValueKind != JsonValueKind.True ||
            !IsString(webhook, "code", "00") ||
            !webhook.TryGetProperty("data", out var data) ||
            !IsString(data, "code", "00") ||
            !IsString(data, "currency", "VND") ||
            !IsString(data, "paymentLinkId", expectedPaymentLinkId))
            return false;

        if (!data.TryGetProperty("orderCode", out var orderCode) ||
            orderCode.ValueKind != JsonValueKind.Number ||
            !orderCode.TryGetInt64(out var reportedOrderCode) ||
            reportedOrderCode != expectedOrderCode ||
            !data.TryGetProperty("amount", out var amount) ||
            amount.ValueKind != JsonValueKind.Number ||
            !amount.TryGetInt64(out var reportedAmount) ||
            reportedAmount != expectedAmountVnd)
            return false;

        // A bank transaction identifier is needed for downstream event deduplication.
        return data.TryGetProperty("reference", out var reference) &&
            reference.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(reference.GetString());
    }

    private static bool IsString(JsonElement value, string name, string expected)
    {
        return value.TryGetProperty(name, out var field) &&
            field.ValueKind == JsonValueKind.String &&
            string.Equals(field.GetString(), expected, StringComparison.Ordinal);
    }
}
