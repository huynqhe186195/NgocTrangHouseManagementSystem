using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Volo.Abp.Authorization;
using Volo.Abp.Uow;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Isolated staging API: one explicitly approved and fully attested
/// month-end new-contract transfer, every alternative remains denied.
/// </summary>
[ApiController]
[Authorize]
[Route("api/slice-b-staging/contracts/{contractId:guid}/room-changes")]
public sealed class SliceBStagingRoomChangesController : ControllerBase
{
    private readonly IHostEnvironment _host;
    private readonly IConfiguration _config;
    private readonly SliceBStagingRoomChangeRequests? _requests;

    public SliceBStagingRoomChangesController(
        IHostEnvironment host, IConfiguration config,
        SliceBStagingRoomChangeRequests? requests = null)
    {
        _host = host;
        _config = config;
        _requests = requests;
    }

    [HttpGet]
    [UnitOfWork(true)]
    public Task<SliceBRoomChangeRequestRead[]> GetListAsync(
        [FromRoute] Guid contractId)
    {
        RequireIsolatedStaging();
        return _requests!.GetListAsync(contractId);
    }

    [HttpGet("{requestId:guid}")]
    [UnitOfWork(true)]
    public Task<SliceBRoomChangeRequestRead> GetAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId)
    {
        RequireIsolatedStaging();
        return _requests!.GetAsync(contractId, requestId);
    }

    [HttpGet("{requestId:guid}/live-readiness")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeLiveReadiness> GetLiveReadinessAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId)
    {
        RequireIsolatedStaging();
        return _requests!.GetLiveReadinessAsync(contractId, requestId);
    }

    [HttpPost]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeRequestRead> SubmitAsync(
        [FromRoute] Guid contractId,
        [FromBody] SliceBRoomChangeSubmitInput input)
    {
        RequireIsolatedStaging();
        return _requests!.SubmitAsync(contractId, input);
    }

    [HttpPost("{requestId:guid}/tenant-roster")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeRosterRead> CaptureRosterAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId)
    {
        RequireIsolatedStaging();
        return _requests!.CaptureRosterAsync(contractId, requestId);
    }

    [HttpGet("{requestId:guid}/tenant-roster")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeRosterRead> GetRosterAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId)
    {
        RequireIsolatedStaging();
        return _requests!.GetRosterAsync(contractId, requestId);
    }

    [HttpPost("{requestId:guid}/business-policy-proposal")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeBusinessPolicyRead> ProposeBusinessPolicyAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId,
        [FromBody] SliceBRoomChangeBusinessPolicyInput input)
    {
        RequireIsolatedStaging();
        return _requests!.ProposeBusinessPolicyAsync(contractId, requestId, input);
    }

    [HttpGet("{requestId:guid}/business-policy-proposal")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeBusinessPolicyRead> GetBusinessPolicyAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId)
    {
        RequireIsolatedStaging();
        return _requests!.GetBusinessPolicyAsync(contractId, requestId);
    }

    [HttpPost("{requestId:guid}/business-policy-review")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeBusinessPolicyReviewRead> ReviewBusinessPolicyAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId,
        [FromBody] SliceBRoomChangeBusinessPolicyReviewInput input)
    {
        RequireIsolatedStaging();
        return _requests!.ReviewBusinessPolicyAsync(contractId, requestId, input);
    }

    [HttpGet("{requestId:guid}/business-policy-review")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeBusinessPolicyReviewRead> GetBusinessPolicyReviewAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId)
    {
        RequireIsolatedStaging();
        return _requests!.GetBusinessPolicyReviewAsync(contractId, requestId);
    }

    // Root-reviewed OI-07 pricing candidate, NOT a financial writer.
    [HttpPost("{requestId:guid}/day-pricing-rule-candidate")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeDayPricingRuleRead> ProposeDayPricingRuleAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId,
        [FromBody] SliceBRoomChangeDayPricingRuleInput input)
    {
        RequireIsolatedStaging();
        return _requests!.ProposeDayPricingRuleAsync(contractId, requestId, input);
    }

    [HttpGet("{requestId:guid}/day-pricing-rule-candidate")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeDayPricingRuleRead> GetDayPricingRuleAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId)
    {
        RequireIsolatedStaging();
        return _requests!.GetDayPricingRuleAsync(contractId, requestId);
    }

    [HttpPost("{requestId:guid}/physical-handover")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeHandoverRead> AttestHandoverAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId,
        [FromBody] SliceBRoomChangeHandoverInput input)
    {
        RequireIsolatedStaging();
        return _requests!.AttestHandoverAsync(contractId, requestId, input);
    }

    [HttpGet("{requestId:guid}/physical-handover")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeHandoverRead> GetHandoverAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId)
    {
        RequireIsolatedStaging();
        return _requests!.GetHandoverAsync(contractId, requestId);
    }

    [HttpGet("{requestId:guid}/execution-readiness")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeExecutionReadiness> GetExecutionReadinessAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId)
    {
        RequireIsolatedStaging();
        return _requests!.GetExecutionReadinessAsync(contractId, requestId);
    }

    [HttpGet("{requestId:guid}/mid-month-financial-journal")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeFinancialJournalRead> GetMidMonthFinancialJournalAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId)
    {
        RequireIsolatedStaging();
        return _requests!.GetMidMonthFinancialJournalAsync(contractId, requestId);
    }


    [HttpPost("{requestId:guid}/signed-pricing-archive")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeArchiveRead> ArchiveExactPricingAsync(
        [FromRoute] Guid contractId,[FromRoute] Guid requestId,
        [FromBody] SliceBRoomChangeSignedPricingArchiveInput input)
    {
        RequireIsolatedStaging();
        return _requests!.ArchiveExactPricingAsync(
            contractId,requestId,input?.SignedApprovalReference);
    }

    [HttpPost("{requestId:guid}/three-party-handover-archive")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeArchiveRead> ArchiveThreePartyHandoverAsync(
        [FromRoute] Guid contractId,[FromRoute] Guid requestId,
        [FromBody] SliceBRoomChangeThreePartyArchiveInput input)
    {
        RequireIsolatedStaging();
        return _requests!.ArchiveThreePartyHandoverAsync(contractId,requestId,input);
    }

    [HttpPost("{requestId:guid}/classified-d03-archive")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeArchiveRead> ArchiveClassifiedD03Async(
        [FromRoute] Guid contractId,[FromRoute] Guid requestId,
        [FromBody] SliceBRoomChangeD03ClassificationInput input)
    {
        RequireIsolatedStaging();
        return _requests!.ArchiveClassifiedD03Async(contractId,requestId,input);
    }

    [HttpPost("{requestId:guid}/post-d03-adjustment")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeD03PostingRead> PostClassifiedD03Async(
        [FromRoute] Guid contractId,[FromRoute] Guid requestId,
        [FromBody] SliceBRoomChangeD03PostingInput input)
    {
        RequireIsolatedStaging();
        return _requests!.PostClassifiedD03Async(
            contractId,requestId,input?.AdjustmentId ?? Guid.Empty);
    }

    // Root-only H4 PROPOSAL: completed internal deposit allocations are
    // checked, but NO top-up is collected and NO refund/credit is posted.
    [HttpPost("{requestId:guid}/h4-disposition-intent")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public async Task<IActionResult> CaptureH4DispositionIntentAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId,
        [FromBody] SliceBRoomChangeH4IntentInput input)
    {
        try
        {
            RequireIsolatedStaging();
            return Ok(await _requests!.CaptureH4DispositionIntentAsync(
                contractId, requestId, input));
        }
        catch (AbpAuthorizationException) { return Forbid(); }
    }

    [HttpGet("{requestId:guid}/h4-disposition-intent")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public async Task<IActionResult> GetH4DispositionIntentAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId)
    {
        try
        {
            RequireIsolatedStaging();
            return Ok(await _requests!.GetH4DispositionIntentAsync(
                contractId, requestId));
        }
        catch (AbpAuthorizationException) { return Forbid(); }
    }

    // Original deferred H4 obligation read; root only, no bank-settled claim.
    [HttpGet("{requestId:guid}/h4-outstanding-obligation")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public async Task<IActionResult> GetH4OutstandingObligationAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId)
    {
        try
        {
            RequireIsolatedStaging();
            return Ok(await _requests!.GetH4OutstandingObligationAsync(
                contractId, requestId));
        }
        catch (AbpAuthorizationException) { return Forbid(); }
    }

    // D04 independently reviewed manual bank statement / completed deposit
    // payment. Review itself neither settles money nor initiates a bank transfer.
    [HttpPost("{requestId:guid}/d04-bank-evidence-review")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public async Task<IActionResult> ReviewD04BankEvidenceAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId,
        [FromBody] SliceBRoomChangeD04ReviewInput input)
    {
        try
        {
            RequireIsolatedStaging();
            return Ok(await _requests!.ReviewH4BankEvidenceAsync(
                contractId, requestId, input));
        }
        catch (AbpAuthorizationException) { return Forbid(); }
    }

    // Owner-only append-only D04 journal; source amounts are DB-resolved,
    // never accepted from a client-provided 'paid' boolean.
    [HttpPost("{requestId:guid}/d04-post-settlement")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public async Task<IActionResult> PostD04SettlementAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId,
        [FromBody] SliceBRoomChangeD04PostingInput input)
    {
        try
        {
            RequireIsolatedStaging();
            return Ok(await _requests!.PostH4SettlementAsync(
                contractId, requestId, input));
        }
        catch (AbpAuthorizationException) { return Forbid(); }
    }

    [HttpGet("{requestId:guid}/d04-settlement-balance")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public async Task<IActionResult> GetD04SettlementBalanceAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId)
    {
        try
        {
            RequireIsolatedStaging();
            return Ok(await _requests!.GetH4SettlementBalanceAsync(
                contractId, requestId));
        }
        catch (AbpAuthorizationException) { return Forbid(); }
    }

    // Isolated staging only: H4 moves the complete roster and creates ONE
    // original signed outstanding financial obligation, never bank cash.
    [HttpPost("{requestId:guid}/complete-h4-mid-month-room-change")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public async Task<IActionResult> CompleteH4MidMonthRoomChangeAsync(
        [FromRoute] Guid contractId,[FromRoute] Guid requestId,
        [FromBody] SliceBRoomChangeCompletionInput input)
    {
        try
        {
            RequireIsolatedStaging();
            return Ok(await _requests!.CompleteH4MidMonthRoomChangeAsync(
                contractId,requestId,input));
        }
        catch (AbpAuthorizationException) { return Forbid(); }
    }

    [HttpPost("{requestId:guid}/complete-mid-month-room-change")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeCompletionRead> CompleteMidMonthRoomChangeAsync(
        [FromRoute] Guid contractId,[FromRoute] Guid requestId,
        [FromBody] SliceBRoomChangeCompletionInput input)
    {
        RequireIsolatedStaging();
        return _requests!.CompleteMidMonthRoomChangeAsync(
            contractId,requestId,input);
    }

    [HttpPost("{requestId:guid}/complete-room-change")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeCompletionRead> CompleteRoomChangeAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId,
        [FromBody] SliceBRoomChangeCompletionInput input)
    {
        RequireIsolatedStaging();
        return _requests!.CompleteRoomChangeAsync(contractId, requestId, input);
    }

    [HttpPost("{requestId:guid}/policy-hold")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeRequestRead> HoldForPolicyAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId,
        [FromBody] SliceBRoomChangeReviewInput input)
    {
        RequireIsolatedStaging();
        return _requests!.HoldForPolicyAsync(contractId, requestId, input);
    }

    [HttpPost("{requestId:guid}/reject")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeRequestRead> RejectAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId,
        [FromBody] SliceBRoomChangeReviewInput input)
    {
        RequireIsolatedStaging();
        return _requests!.RejectAsync(contractId, requestId, input);
    }

    [HttpPost("{requestId:guid}/cancel")]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeRequestRead> CancelAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId)
    {
        RequireIsolatedStaging();
        return _requests!.CancelAsync(contractId, requestId);
    }

    private void RequireIsolatedStaging()
    {
        if (!_host.IsStaging() ||
            !_config.GetValue<bool>(
                "BuildingManagement:SliceAStaging:Enabled") ||
            _config["BuildingManagement:SliceAStaging:Activation"] !=
                "I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES" ||
            _requests is null)
            throw Denied();

        var identity = new NpgsqlConnectionStringBuilder(
            _config.GetConnectionString("Default"));
        var building = new NpgsqlConnectionStringBuilder(
            _config.GetConnectionString("BuildingManagement"));
        if (!identity.Database.StartsWith(
                "bm_slicea_staging_identity_", StringComparison.OrdinalIgnoreCase) ||
            !building.Database.StartsWith(
                "bm_slicea_staging_buildings_", StringComparison.OrdinalIgnoreCase) ||
            identity.Database == building.Database)
            throw Denied();
    }

    private static AbpAuthorizationException Denied()
        => new("Room-change requests require two isolated staging databases.");
}

public sealed record SliceBRoomChangeSignedPricingArchiveInput(
    string? SignedApprovalReference);

public sealed record SliceBRoomChangeD03PostingInput(Guid AdjustmentId);
