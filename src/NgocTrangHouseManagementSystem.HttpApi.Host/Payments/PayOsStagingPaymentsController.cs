using System;
using System.IO;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using BuildingManagement.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace NgocTrangHouseManagementSystem.Payments;

/// <summary>
/// Source-wired, default-OFF staging payOS HTTP ingress. Production cannot
/// activate its DB writer, even when sent a valid payOS-signed webhook.
/// Nothing here marks a Payment completed or posts a D04 financial journal.
/// </summary>
[ApiController]
[Authorize]
[Route("api/payments/payos/staging")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class PayOsStagingPaymentsController : ControllerBase
{
    private const string Activation = "I_ACCEPT_ISOLATED_PAYOS_STAGING_ONLY";
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly PayOsStagingCheckoutService? _checkout;
    private readonly PayOsStagingWebhookInboxWriter? _inbox;
    private readonly PayOsStagingReconciliationService? _reconciliation;

    public PayOsStagingPaymentsController(
        IHostEnvironment environment, IConfiguration configuration,
        PayOsStagingCheckoutService? checkout = null,
        PayOsStagingWebhookInboxWriter? inbox = null,
        PayOsStagingReconciliationService? reconciliation = null)
    {
        _environment = environment;
        _configuration = configuration;
        _checkout = checkout;
        _inbox = inbox;
        _reconciliation = reconciliation;
    }

    [HttpPost("checkout/{paymentId:guid}")]
    public async Task<IActionResult> CheckoutAsync([FromRoute] Guid paymentId)
    {
        if (!IsEnabled() || _checkout is null) return NotFound();
        try
        {
            // The caller controls only the existing Payment ID; all monetary
            // data and scope derive from EF + ABP permission/membership.
            var result = await _checkout.StartOrRecoverAsync(
                paymentId, HttpContext.RequestAborted);
            return Ok(result);
        }
        catch (HttpRequestException)
        {
            // Network ambiguity is NOT permission to reserve another order.
            return StatusCode(503, new { status = "ProviderRetrySameOrder" });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new { status = "ProviderOrPaymentRequiresReview" });
        }
    }

    [HttpPost("reconcile/{paymentId:guid}")]
    public async Task<IActionResult> ReconcileAsync([FromRoute] Guid paymentId)
    {
        if (!IsEnabled() || _reconciliation is null) return NotFound();
        try
        {
            var result = await _reconciliation.CheckAsync(
                paymentId, HttpContext.RequestAborted);
            // A successful check is NOT payment completion.
            return Accepted(result);
        }
        catch (HttpRequestException)
        {
            return StatusCode(503, new { status = "ProviderRetryLater" });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new { status = "RequiresIndependentReview" });
        }
    }

    // Provider webhook is unauthenticated at the HTTP identity layer.
    // Trust ONLY a valid payOS HMAC under the server-held checksum key.
    [AllowAnonymous]
    [HttpPost("webhook")]
    [RequestSizeLimit(PayOsWebhookIntakeGuard.MaximumBodyBytes)]
    public async Task<IActionResult> WebhookAsync()
    {
        if (!IsEnabled() || _inbox is null) return NotFound();
        if (Request.ContentType is null ||
            !Request.ContentType.StartsWith("application/json",
                StringComparison.OrdinalIgnoreCase))
            return StatusCode(415);
        if (Request.ContentLength > PayOsWebhookIntakeGuard.MaximumBodyBytes)
            return StatusCode(413);

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await Request.Body.ReadAsync(
                   chunk.AsMemory(), HttpContext.RequestAborted)) > 0)
        {
            if (count + buffer.Length > PayOsWebhookIntakeGuard.MaximumBodyBytes)
                return StatusCode(413);
            buffer.Write(chunk, 0, count);
        }

        var payload = buffer.ToArray();
        if (payload.Length == 0) return BadRequest();

        try
        {
            var outcome = await _inbox.RecordAsync(
                payload, HttpContext.RequestAborted);
            if (outcome is PayOsWebhookIngestResult.StoredPendingReconciliation or
                PayOsWebhookIngestResult.StoredManualReview or
                PayOsWebhookIngestResult.Duplicate)
                return Ok(new { success = true });

            // payOS confirm-webhook sends a signed, documented TEST transfer,
            // not a customer PaymentIntent. Do not ACK arbitrary unknown money.
            if (outcome == PayOsWebhookIngestResult.UnknownIntent &&
                IsDocumentedProviderVerificationProbe(payload))
                return Ok(new { success = true });

            return outcome switch
            {
                PayOsWebhookIngestResult.Rejected => BadRequest(),
                PayOsWebhookIngestResult.Conflict => Conflict(new { success = false }),
                _ => StatusCode(503, new { success = false })
            };
        }
        catch (JsonException)
        {
            return BadRequest();
        }
        // Other DB failures must remain non-2xx so payOS retries the webhook;
        // do not conceal outages behind a success response.
    }

    private bool IsDocumentedProviderVerificationProbe(byte[] raw)
    {
        var key = _configuration["BuildingManagement:PayOsStaging:ChecksumKey"];
        if (string.IsNullOrWhiteSpace(key)) return false;
        try
        {
            using var json = JsonDocument.Parse(raw);
            if (!PayOsPaymentWebhookSignature.IsValid(json.RootElement, key))
                return false;
            var data = json.RootElement.GetProperty("data");
            return data.GetProperty("orderCode").GetInt64() == 123 &&
                   data.GetProperty("amount").GetInt64() == 3000 &&
                   data.GetProperty("description").GetString() == "VQRIO123" &&
                   data.GetProperty("reference").GetString() == "TF230204212323";
        }
        catch (Exception ex) when (ex is JsonException or
            InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return false;
        }
    }

    private bool IsEnabled() =>
        _environment.IsStaging() &&
        _configuration.GetValue<bool>("BuildingManagement:PayOsStaging:Enabled") &&
        _configuration["BuildingManagement:PayOsStaging:Activation"] == Activation;
}
