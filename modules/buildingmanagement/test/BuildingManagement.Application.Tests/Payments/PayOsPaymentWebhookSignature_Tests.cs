using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingManagement.Payments;
using Xunit;

namespace BuildingManagement.Payments.Tests;

public class PayOsPaymentWebhookSignature_Tests
{
    [Fact]
    public void Accepts_signed_payment_data_independent_of_json_property_order()
    {
        const string key = "local-test-key-not-a-real-secret";
        // payOS payment webhook signs the sorted properties of data, not the entire envelope.
        const string canonical = "amount=150000&code=00&orderCode=123&reference=ABC";
        var signature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        using var document = JsonDocument.Parse(
            "{\"success\":true,\"data\":{\"reference\":\"ABC\",\"orderCode\":123,\"code\":\"00\",\"amount\":150000},\"signature\":\"" + signature + "\"}");
        Assert.True(PayOsPaymentWebhookSignature.IsValid(document.RootElement, key));
    }

    [Fact]
    public void Rejects_wrong_amount_or_wrong_key_or_malformed_signature()
    {
        const string key = "local-test-key-not-a-real-secret";
        const string canonical = "amount=150000&code=00&orderCode=123&reference=ABC";
        var signature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        using var altered = JsonDocument.Parse(
            "{\"data\":{\"amount\":150001,\"code\":\"00\",\"orderCode\":123,\"reference\":\"ABC\"},\"signature\":\"" + signature + "\"}");
        Assert.False(PayOsPaymentWebhookSignature.IsValid(altered.RootElement, key));
        using var original = JsonDocument.Parse(
            "{\"data\":{\"amount\":150000,\"code\":\"00\",\"orderCode\":123,\"reference\":\"ABC\"},\"signature\":\"" + signature + "\"}");
        Assert.False(PayOsPaymentWebhookSignature.IsValid(original.RootElement, "other-key"));
        using var invalid = JsonDocument.Parse("{\"data\":{},\"signature\":\"not-hex\"}");
        Assert.False(PayOsPaymentWebhookSignature.IsValid(invalid.RootElement, key));
    }

    [Fact]
    public void Rejects_missing_data_and_signature()
    {
        using var missing = JsonDocument.Parse("{\"success\":true}");
        Assert.False(PayOsPaymentWebhookSignature.IsValid(missing.RootElement, "key"));
    }
}
