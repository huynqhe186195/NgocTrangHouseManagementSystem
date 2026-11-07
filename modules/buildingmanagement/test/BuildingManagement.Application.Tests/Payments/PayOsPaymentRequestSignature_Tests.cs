using System;
using System.Security.Cryptography;
using System.Text;
using BuildingManagement.Payments;
using Xunit;

namespace BuildingManagement.Payments.Tests;

public class PayOsPaymentRequestSignature_Tests
{
    [Fact]
    public void Creates_exact_payos_canonical_payment_request_signature()
    {
        const long order = 123456;
        const long amount = 150000;
        const string description = "PHONG301";
        const string key = "test-checksum-key-not-a-secret";
        var returnUrl = new Uri("https://example.test/payments/return");
        var cancelUrl = new Uri("https://example.test/payments/cancel");
        const string signedMessage =
            "amount=150000&cancelUrl=https://example.test/payments/cancel" +
            "&description=PHONG301&orderCode=123456" +
            "&returnUrl=https://example.test/payments/return";
        var independentExpected = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(signedMessage)))
            .ToLowerInvariant();

        Assert.Equal(independentExpected,
            PayOsPaymentRequestSignature.Create(order, amount, description,
                returnUrl, cancelUrl, key));
        Assert.Equal(64, independentExpected.Length);
    }

    [Fact]
    public void Rejects_non_positive_amount_or_order_and_missing_credentials()
    {
        var valid = new Uri("https://example.test/return");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PayOsPaymentRequestSignature.Create(0, 100, "rent", valid, valid, "key"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PayOsPaymentRequestSignature.Create(123, 0, "rent", valid, valid, "key"));
        Assert.Throws<ArgumentException>(() =>
            PayOsPaymentRequestSignature.Create(123, 100, "rent", valid, valid, ""));
        Assert.Throws<ArgumentException>(() =>
            PayOsPaymentRequestSignature.Create(123, 100, "", valid, valid, "key"));
    }

    [Fact]
    public void Rejects_insecure_callback_urls()
    {
        var https = new Uri("https://example.test/return");
        var http = new Uri("http://example.test/cancel");
        Assert.Throws<ArgumentException>(() =>
            PayOsPaymentRequestSignature.Create(123, 100, "rent", https, http, "key"));
        Assert.Throws<ArgumentException>(() =>
            PayOsPaymentRequestSignature.Create(123, 100, "rent",
                new Uri("https://alice:password@example.test/return"), https, "key"));
    }
}
