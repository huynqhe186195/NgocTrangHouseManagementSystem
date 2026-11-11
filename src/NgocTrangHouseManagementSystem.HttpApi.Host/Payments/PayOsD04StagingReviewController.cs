using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Volo.Abp.Authorization;
using Volo.Abp.Uow;

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
// Every action uses ABP's context provider, but the reviewed writer owns
// the SERIALIZABLE financial commit. A nested ambient ABP transaction would
// break the Contract->Payment lock order and the cross-DB ACK protocol.
[UnitOfWork(isTransactional: false)]
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
            !form.ReviewerAttestsBankStatementMatched)
            return BadRequest();

        // Memory cap + no logging. The archive-backed application facade
        // issues and verifies the reference SERVER-SIDE. The legacy form
        // value is optional and NOT authoritative; a direct unwrapped
        // manual service still fails closed on empty/missing proof.
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
        catch (AbpAuthorizationException) { return StatusCode(403); }
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
        catch (AbpAuthorizationException) { return StatusCode(403); }
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
        catch (AbpAuthorizationException) { return StatusCode(403); }
        catch (InvalidOperationException) { return Conflict(); }
    }

    // Fail closed at the HTTP boundary too, not just when Host registers DI.
    // A permissive/inadvertent controller service registration must never
    // turn a configuration flag into a cash-posting endpoint on shared data.
    private bool Enabled() =>
        _host.IsStaging() &&
        _config.GetValue<bool>("BuildingManagement:PayOsD04B:Enabled") &&
        _config["BuildingManagement:PayOsD04B:Activation"] ==
            "I_ACCEPT_DISPOSABLE_D04B_DUAL_AUTH_ONLY" &&
        !_config.GetValue<bool>("BuildingManagement:PayOsStaging:Enabled") &&
        ExactDisposableDatabase(_config.GetConnectionString("Default"),
            "payos_d04_identity_test") &&
        ExactDisposableDatabase(_config.GetConnectionString("BuildingManagement"),
            "payos_d04_legacy_test") &&
        ExactDisposableDatabase(_config.GetConnectionString("PayOsD04BStaging"),
            "payos_ef_migration_test");

    private static bool ExactDisposableDatabase(string? input, string name)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        try
        {
            var db = new NpgsqlConnectionStringBuilder(input);
            return db.Host is ("127.0.0.1" or "localhost") &&
                   db.Port == 15432 &&
                   string.Equals(db.Database, name, StringComparison.Ordinal) &&
                   (db.Username == "postgres" ||
                    // P1.3: the legacy money-writer and staging-ACK slots may
                    // run under the restricted least-privilege HOST runtime
                    // roles (no DDL, no superuser) whose grants match the
                    // full canonical Host D04-B SQL (review + approval +
                    // writer + ACK). Distinct from the narrower P1.2
                    // d04_money_writer_* rehearsal roles by design.
                    (name == "payos_d04_legacy_test" &&
                     db.Username == "d04_d04b_host_legacy_test") ||
                    (name == "payos_ef_migration_test" &&
                     db.Username == "d04_d04b_host_staging_test")) &&
                   db.Password == "disposable-only";
        }
        catch (ArgumentException) { return false; }
    }
}

public sealed class PayOsD04ReviewForm
{
    public string BankReference { get; set; } = string.Empty;
    public long AmountVnd { get; set; }
    public IFormFile? Document { get; set; }
    public string ProtectedArchiveReference { get; set; } = string.Empty;
    public bool ReviewerAttestsBankStatementMatched { get; set; }
}
