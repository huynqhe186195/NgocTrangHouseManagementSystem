using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Volo.Abp.Authorization;

namespace NgocTrangHouseManagementSystem.Payments;

/// <summary>
/// Source-only HTTP endpoints for D04-B review/Owner/posting. Default OFF:
/// the two required interfaces are NOT currently registered in Host DI.
/// A mere activation flag cannot create money or bypass the separately
/// required, three-database staging Identity/Building transaction gates.
/// Never enable a financial endpoint by injecting permissive test doubles.
/// </summary>
[ApiController]
[Authorize]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/payments/payos/d04/staging")]
public sealed class PayOsD04StagingReviewController : ControllerBase
{
    private readonly IHostEnvironment _host;
    private readonly IConfiguration _config;
    private readonly IPayOsD04ManualEvidenceService? _manual;
    private readonly IPayOsD04ReviewedPostingCoordinator? _coordinator;

    public PayOsD04StagingReviewController(
        IHostEnvironment host, IConfiguration config,
        IPayOsD04ManualEvidenceService? manual = null,
        IPayOsD04ReviewedPostingCoordinator? coordinator = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _manual = manual;
        _coordinator = coordinator;
    }

    // A separate, authentically signed-in BANK REVIEWER must inspect
    // independent statement evidence, upload the exact file to a protected
    // external archive and attest it matches amount+bank reference. D04-B
    // staging tests use synthetic files only. No real personal/financial
    // document is accepted until archival and privacy review is complete.
    [HttpPost("review/{intentId:guid}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(6_291_456)]
    public async Task<IActionResult> ReviewAsync(
        [FromRoute] Guid intentId,
        [FromForm] PayOsD04ReviewForm form)
    {
        if (!Enabled() || _manual is null) return NotFound();
        if (intentId == Guid.Empty || form.Document is null ||
            form.Document.Length is < 256 or > 5_242_880 ||
            string.IsNullOrWhiteSpace(form.BankReference) ||
            string.IsNullOrWhiteSpace(form.ProtectedArchiveReference) ||
            !form.ReviewerAttestsBankStatementMatched)
            return BadRequest();

        // Memory cap + no logging. Actual durability is the separate
        // protected archive referenced by the immutable bank review.
        await using var stream = new MemoryStream();
        await form.Document.CopyToAsync(stream, HttpContext.RequestAborted);
        if (stream.Length != form.Document.Length ||
            stream.Length > 5_242_880)
            return BadRequest();
        try
        {
            var id = await _manual.RecordIndependentBankReviewAsync(intentId,
                new PayOsD04ManualBankStatementSubmission(
                    form.BankReference, form.AmountVnd, stream.ToArray(),
                    form.ProtectedArchiveReference,
                    form.ReviewerAttestsBankStatementMatched),
                HttpContext.RequestAborted);
            return Accepted(new { ReviewId = id, Status = "RequiresDifferentOwnerApproval" });
        }
        catch (AbpAuthorizationException) { return Forbid(); }
        catch (InvalidOperationException) { return Conflict(); }
    }

    [HttpPost("approve/{bankReviewId:guid}")]
    public async Task<IActionResult> ApproveAsync([FromRoute] Guid bankReviewId)
    {
        if (!Enabled() || _manual is null) return NotFound();
        if (bankReviewId == Guid.Empty) return BadRequest();
        try
        {
            var id = await _manual.ApproveReviewedDepositAsync(
                bankReviewId, HttpContext.RequestAborted);
            return Accepted(new { ApprovalId = id, Status = "ApprovedPendingFinancialPosting" });
        }
        catch (AbpAuthorizationException) { return Forbid(); }
        catch (InvalidOperationException) { return Conflict(); }
    }

    [HttpPost("post/{intentId:guid}")]
    public async Task<IActionResult> PostAsync([FromRoute] Guid intentId)
    {
        if (!Enabled() || _coordinator is null) return NotFound();
        if (intentId == Guid.Empty) return BadRequest();
        try
        {
            var result = await _coordinator.PostApprovedAsync(
                intentId, HttpContext.RequestAborted);
            if (result.AckPending)
                return Accepted(new
                {
                    result.ReceiptId, result.OutboxId,
                    Status = "LegacyCommittedAckPendingRetrySameIntent"
                });
            return Ok(new
            {
                result.ReceiptId, result.OutboxId,
                Status = "LegacyCommittedAndStagingAcknowledged"
            });
        }
        catch (AbpAuthorizationException) { return Forbid(); }
        catch (InvalidOperationException) { return Conflict(); }
    }

    private bool Enabled() =>
        _host.IsStaging() &&
        _config.GetValue<bool>("BuildingManagement:PayOsD04B:Enabled") &&
        _config["BuildingManagement:PayOsD04B:Activation"] ==
            "I_ACCEPT_DISPOSABLE_D04B_DUAL_AUTH_ONLY";
}

public sealed class PayOsD04ReviewForm
{
    public string BankReference { get; set; } = string.Empty;
    public long AmountVnd { get; set; }
    public IFormFile? Document { get; set; }
    public string ProtectedArchiveReference { get; set; } = string.Empty;
    public bool ReviewerAttestsBankStatementMatched { get; set; }
}
