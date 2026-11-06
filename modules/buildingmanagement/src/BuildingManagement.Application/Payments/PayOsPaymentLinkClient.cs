using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BuildingManagement.Payments;

/// <summary>
/// Provider transport only. Callers MUST persist/authorize a unique intent
/// before invoking this adapter. Neither method writes a receipt or journal.
/// Credentials must come from the server secret store, never request DTOs.
/// </summary>
public sealed class PayOsPaymentLinkClient
{
    private static readonly Uri MerchantApi =
        new("https://api-merchant.payos.vn/");

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _apiKey;
    private readonly string _checksumKey;

    public PayOsPaymentLinkClient(
        HttpClient http,
        string clientId,
        string apiKey,
        string checksumKey)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _clientId = RequireSecret(clientId, nameof(clientId));
        _apiKey = RequireSecret(apiKey, nameof(apiKey));
        _checksumKey = RequireSecret(checksumKey, nameof(checksumKey));
    }

    public async Task<PayOsCheckoutResult> CreateAsync(
        PayOsCheckoutCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ExpiresAtUtc.Kind != DateTimeKind.Utc ||
            command.ExpiresAtUtc <= DateTime.UtcNow)
            throw new ArgumentException("Expiry must be a future UTC timestamp.");
        var unixExpiry = new DateTimeOffset(command.ExpiresAtUtc).ToUnixTimeSeconds();
        if (unixExpiry > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(command), "Expiry cannot exceed signed 32-bit Unix time.");

        var signature = PayOsPaymentRequestSignature.Create(
            command.OrderCode, command.AmountVnd, command.Description,
            command.ReturnUrl, command.CancelUrl, _checksumKey);

        var payload = new
        {
            orderCode = command.OrderCode,
            amount = command.AmountVnd,
            description = command.Description,
            returnUrl = command.ReturnUrl.AbsoluteUri,
            cancelUrl = command.CancelUrl.AbsoluteUri,
            expiredAt = (int)unixExpiry,
            signature
        };

        using var request = NewRequest(HttpMethod.Post, "v2/payment-requests");
        request.Content = JsonContent.Create(payload);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var root = document.RootElement;
        if (!StringIs(root, "code", "00") ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !LongIs(data, "orderCode", command.OrderCode) ||
            !LongIs(data, "amount", command.AmountVnd) ||
            !StringIs(data, "currency", "VND") ||
            !StringIs(data, "status", "PENDING"))
            throw new InvalidOperationException("payOS checkout response does not match the requested intent.");

        var linkId = RequiredString(data, "paymentLinkId");
        var checkoutUrlString = RequiredString(data, "checkoutUrl");
        if (!Uri.TryCreate(checkoutUrlString, UriKind.Absolute, out var checkoutUrl) ||
            checkoutUrl.Scheme != Uri.UriSchemeHttps ||
            checkoutUrl.Host != "pay.payos.vn" ||
            !string.IsNullOrEmpty(checkoutUrl.UserInfo))
            throw new InvalidOperationException("Unexpected payOS checkout URL.");

        // The caller must persist linkId and QR data transactionally before
        // returning a checkout link. Never mark the intent as PAID here.
        var qrCode = RequiredString(data, "qrCode");
        return new PayOsCheckoutResult(
            command.OrderCode, command.AmountVnd, linkId, checkoutUrl, qrCode);
    }

    /// <summary>
    /// Read-only provider lookup for reconciliation after an ambiguous
    /// timeout or webhook. Provider status alone is NOT settlement approval.
    /// </summary>
    public async Task<PayOsPaymentLinkSnapshot> GetAsync(
        long orderCode, CancellationToken cancellationToken = default)
    {
        if (orderCode <= 0) throw new ArgumentOutOfRangeException(nameof(orderCode));
        using var request = NewRequest(HttpMethod.Get,
            "v2/payment-requests/" + orderCode.ToString(CultureInfo.InvariantCulture));
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!StringIs(root, "code", "00") ||
            !root.TryGetProperty("data", out var data) ||
            !LongIs(data, "orderCode", orderCode) ||
            !data.TryGetProperty("amount", out var amount) ||
            !amount.TryGetInt64(out var amountVnd) ||
            amountVnd <= 0 ||
            !data.TryGetProperty("amountPaid", out var paid) ||
            !paid.TryGetInt64(out var amountPaid) ||
            !data.TryGetProperty("amountRemaining", out var remaining) ||
            !remaining.TryGetInt64(out var amountRemaining) ||
            amountPaid < 0 || amountRemaining < 0)
            throw new InvalidOperationException("Invalid payOS reconciliation response.");

        var id = RequiredString(data, "id");
        var status = RequiredString(data, "status");
        if (status is not ("PENDING" or "PROCESSING" or "PAID" or "CANCELLED"))
            throw new InvalidOperationException("Unknown payOS payment status.");

        return new PayOsPaymentLinkSnapshot(orderCode, amountVnd, amountPaid,
            amountRemaining, id, status);
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string relativePath)
    {
        var request = new HttpRequestMessage(method, new Uri(MerchantApi, relativePath));
        request.Headers.Add("x-client-id", _clientId);
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static string RequireSecret(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\r') || value.Contains('\n'))
            throw new ArgumentException("Missing or invalid server-side payOS credential.", name);
        return value;
    }

    private static string RequiredString(JsonElement data, string name)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException("Missing required payOS response field: " + name);
        return value.GetString()!;
    }

    private static bool StringIs(JsonElement data, string name, string expected) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static bool LongIs(JsonElement data, string name, long expected) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number) &&
        number == expected;
}

public sealed record PayOsCheckoutCommand(
    long OrderCode, long AmountVnd, string Description,
    Uri ReturnUrl, Uri CancelUrl, DateTime ExpiresAtUtc);

public sealed record PayOsCheckoutResult(
    long OrderCode, long AmountVnd, string PaymentLinkId,
    Uri CheckoutUrl, string QrCode);

public sealed record PayOsPaymentLinkSnapshot(
    long OrderCode, long AmountVnd, long AmountPaidVnd,
    long AmountRemainingVnd, string PaymentLinkId, string Status);
