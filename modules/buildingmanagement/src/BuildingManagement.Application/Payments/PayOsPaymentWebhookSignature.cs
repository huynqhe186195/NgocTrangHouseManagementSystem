using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BuildingManagement.Payments;

/// <summary>
/// Validates payOS payment-request webhook signatures only. Does not settle a Payment.
/// See https://payos.vn/docs/tich-hop-webhook/kiem-tra-du-lieu-voi-signature/
/// Payout signatures use a different algorithm and MUST NOT use this helper.
/// </summary>
public static class PayOsPaymentWebhookSignature
{
    public static bool IsValid(JsonElement webhook, string checksumKey)
    {
        if (string.IsNullOrWhiteSpace(checksumKey) ||
            webhook.ValueKind != JsonValueKind.Object ||
            !webhook.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !webhook.TryGetProperty("signature", out var signature) ||
            signature.ValueKind != JsonValueKind.String)
            return false;

        var supplied = signature.GetString();
        if (supplied is null || supplied.Length != 64)
            return false;

        byte[] actual;
        try { actual = Convert.FromHexString(supplied); }
        catch (FormatException) { return false; }

        var fields = data.EnumerateObject()
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Name + "=" + CanonicalValue(x.Value));
        var content = string.Join("&", fields);
        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(checksumKey), Encoding.UTF8.GetBytes(content));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string CanonicalValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return "";
            case JsonValueKind.String:
            {
                var text = value.GetString() ?? "";
                return text is "undefined" or "null" ? "" : text;
            }
            case JsonValueKind.True: return "true";
            case JsonValueKind.False: return "false";
            case JsonValueKind.Number: return value.GetRawText();
            case JsonValueKind.Array:
                return JsonSerializer.Serialize(value.EnumerateArray()
                    .Select(SortObjectRecursively).ToArray());
            case JsonValueKind.Object:
                return value.GetRawText();
            default:
                throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static object? SortObjectRecursively(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .ToDictionary(x => x.Name, x => SortObjectRecursively(x.Value)),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(SortObjectRecursively).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
