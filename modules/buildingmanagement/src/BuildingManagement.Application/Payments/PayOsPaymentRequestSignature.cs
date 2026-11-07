using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BuildingManagement.Payments;

/// <summary>
/// payOS v2 payment-request creation signature. Pure utility: makes no network
/// request and does not authorize an invoice, create an intent or settle a payment.
/// </summary>
public static class PayOsPaymentRequestSignature
{
    public static string Create(
        long orderCode,
        long amountVnd,
        string description,
        Uri returnUrl,
        Uri cancelUrl,
        string checksumKey)
    {
        if (orderCode <= 0) throw new ArgumentOutOfRangeException(nameof(orderCode));
        if (amountVnd <= 0) throw new ArgumentOutOfRangeException(nameof(amountVnd));
        if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("Description is required.", nameof(description));
        if (string.IsNullOrWhiteSpace(checksumKey)) throw new ArgumentException("Checksum key is required.", nameof(checksumKey));
        RequireHttps(returnUrl, nameof(returnUrl));
        RequireHttps(cancelUrl, nameof(cancelUrl));

        // The field order is explicitly prescribed by the payOS payment-requests API.
        // Values are not URL-encoded in this HMAC message.
        var source = "amount=" + amountVnd.ToString(CultureInfo.InvariantCulture) +
            "&cancelUrl=" + cancelUrl.AbsoluteUri +
            "&description=" + description +
            "&orderCode=" + orderCode.ToString(CultureInfo.InvariantCulture) +
            "&returnUrl=" + returnUrl.AbsoluteUri;
        var digest = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(checksumKey), Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static void RequireHttps(Uri uri, string paramName)
    {
        if (uri is null || !uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("An absolute HTTPS URL without user credentials is required.", paramName);
    }
}
