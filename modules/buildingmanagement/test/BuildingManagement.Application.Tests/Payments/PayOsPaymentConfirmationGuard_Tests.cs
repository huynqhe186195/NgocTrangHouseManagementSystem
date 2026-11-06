using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingManagement.Payments;
using Xunit;

namespace BuildingManagement.Payments.Tests;

public class PayOsPaymentConfirmationGuard_Tests
{
    private const string Key = "local-testing-secret";

    private static JsonDocument Signed(string currency = "VND", long amount = 150000,
        long orderCode = 123, string linkId = "LINK123",
        string reference = "REF1", string code = "00", bool success = true)
    {
        // Mirror payOS alphabetically sorted data signing algorithm.
        var canonical = "amount=" + amount + "&code=" + code + "&currency=" + currency +
            "&orderCode=" + orderCode + "&paymentLinkId=" + linkId + "&reference=" + reference;
        var signature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Key), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var payload = JsonSerializer.Serialize(new
        {
            code = "00",
            success,
            data = new { amount, code, currency, orderCode, paymentLinkId = linkId, reference },
            signature
        });
        return JsonDocument.Parse(payload);
    }

    [Fact]
    public void Signed_matching_vnd_payment_is_eligible_for_durable_reconciliation()
    {
        using var webhook = Signed();
        Assert.True(PayOsPaymentConfirmationGuard.Matches(
            webhook.RootElement, 123, 150000, "LINK123", Key));
        Assert.False(PayOsPaymentConfirmationGuard.Matches(
            webhook.RootElement, 124, 150000, "LINK123", Key));
        Assert.False(PayOsPaymentConfirmationGuard.Matches(
            webhook.RootElement, 123, 150001, "LINK123", Key));
        Assert.False(PayOsPaymentConfirmationGuard.Matches(
            webhook.RootElement, 123, 150000, "OTHER-LINK", Key));
        Assert.False(PayOsPaymentConfirmationGuard.Matches(
            webhook.RootElement, 123, 150000, "LINK123", "wrong-key"));
    }

    [Fact]
    public void Rejects_even_validly_signed_non_vnd_failure_or_missing_bank_reference()
    {
        using var otherCurrency = Signed(currency: "USD");
        Assert.False(PayOsPaymentConfirmationGuard.Matches(
            otherCurrency.RootElement, 123, 150000, "LINK123", Key));
        using var failed = Signed(code: "01");
        Assert.False(PayOsPaymentConfirmationGuard.Matches(
            failed.RootElement, 123, 150000, "LINK123", Key));
        using var falseSuccess = Signed(success: false);
        Assert.False(PayOsPaymentConfirmationGuard.Matches(
            falseSuccess.RootElement, 123, 150000, "LINK123", Key));
        using var noReference = Signed(reference: "");
        Assert.False(PayOsPaymentConfirmationGuard.Matches(
            noReference.RootElement, 123, 150000, "LINK123", Key));
    }
}
