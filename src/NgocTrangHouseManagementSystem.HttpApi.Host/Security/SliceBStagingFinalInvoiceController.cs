using System;
using System.Threading.Tasks;
using BuildingManagement.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Volo.Abp.Authorization;
using Volo.Abp.Uow;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// OPT-IN only: records prepared evidence, an independent design decision,
/// and an issuer-verified candidate on two disposable staging PostgreSQL
/// databases. No production registration, no deposit refund, no closeout.
/// </summary>
[ApiController]
[Authorize]
[Route("api/slice-b-staging/final-invoices")]
public sealed class SliceBStagingFinalInvoiceController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly SliceBStagingFinalInvoiceMutation? _mutation;

    public SliceBStagingFinalInvoiceController(
        IHostEnvironment environment,
        IConfiguration configuration,
        SliceBStagingFinalInvoiceMutation? mutation = null)
    {
        _environment = environment;
        _configuration = configuration;
        _mutation = mutation;
    }

    /// <summary>
    /// Root-reviewed recurring monthly evidence. Explicit opt-in staging
    /// only; a candidate is not an issued invoice or posted debt.
    /// </summary>
    [HttpPost("contracts/{contractId:guid}/monthly-drafts/{draftId:guid}/candidate")]
    [UnitOfWork(true, System.Data.IsolationLevel.RepeatableRead)]
    public Task<SliceBStagedMonthlyInvoiceCandidateRead>
        PrepareMonthlyCandidateAsync(
            [FromRoute] Guid contractId, [FromRoute] Guid draftId,
            [FromBody] MonthlyInvoiceFinancialTermsProposal input)
    {
        RequireIsolatedStaging();
        if (input is null)
            throw Denied();
        return (_mutation ?? throw Denied())
            .PrepareMonthlyCandidateAsync(contractId, draftId, input);
    }

    [HttpPost("contracts/{contractId:guid}/monthly-candidates/{candidateId:guid}/review")]
    [UnitOfWork(true, System.Data.IsolationLevel.RepeatableRead)]
    public Task<SliceBStagedMonthlyReviewRead> ReviewMonthlyCandidateAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid candidateId,
        [FromBody] SliceBMonthlyReviewInput input)
    {
        RequireIsolatedStaging();
        if (input is null)
            throw Denied();
        return (_mutation ?? throw Denied()).ReviewMonthlyCandidateAsync(
            contractId, candidateId, input.Accept, input.Reason);
    }

    [HttpPost("contracts/{contractId:guid}/monthly-candidates/{candidateId:guid}/issue")]
    [UnitOfWork(true, System.Data.IsolationLevel.RepeatableRead)]
    public Task<SliceBStagedMonthlyIssuedRead> IssueMonthlyAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid candidateId)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).IssueMonthlyAsync(
            contractId, candidateId);
    }

    [HttpPost("contracts/{contractId:guid}/monthly-issued/{issuedId:guid}/manual-receipts")]
    [UnitOfWork(true, System.Data.IsolationLevel.Serializable)]
    public Task<SliceBStagedMonthlyReceiptRead> RecordMonthlyReceiptAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid issuedId,
        [FromBody] SliceBFinalInvoiceManualReceiptInput input)
    {
        RequireIsolatedStaging();
        if (input is null)
            throw Denied();
        return (_mutation ?? throw Denied()).RecordMonthlyReceiptAsync(
            contractId, issuedId, input);
    }

    [HttpGet("contracts/{contractId:guid}/monthly-issued/{issuedId:guid}/balance")]
    [UnitOfWork(true, System.Data.IsolationLevel.RepeatableRead)]
    public Task<SliceBStagedMonthlyBalanceRead> GetMonthlyBalanceAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid issuedId,
        [FromQuery] DateOnly? asOfDate = null)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).GetMonthlyBalanceAsync(
            contractId, issuedId, asOfDate);
    }

    [HttpPost("contracts/{contractId:guid}/monthly-issued/{issuedId:guid}/adjustments")]
    [UnitOfWork(true, System.Data.IsolationLevel.RepeatableRead)]
    public Task<SliceBStagedMonthlyAdjustmentRead> ProposeMonthlyAdjustmentAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid issuedId,
        [FromBody] SliceBMonthlyAdjustmentInput input)
    {
        RequireIsolatedStaging();
        if (input is null) throw Denied();
        return (_mutation ?? throw Denied()).ProposeMonthlyAdjustmentAsync(
            contractId, issuedId, input);
    }

    [HttpPost("contracts/{contractId:guid}/monthly-adjustments/{adjustmentId:guid}/review")]
    [UnitOfWork(true, System.Data.IsolationLevel.Serializable)]
    public Task<SliceBStagedMonthlyAdjustmentDecisionRead>
        ReviewMonthlyAdjustmentAsync(
            [FromRoute] Guid contractId, [FromRoute] Guid adjustmentId,
            [FromBody] SliceBMonthlyAdjustmentDecisionInput input)
    {
        RequireIsolatedStaging();
        if (input is null) throw Denied();
        return (_mutation ?? throw Denied()).ReviewMonthlyAdjustmentAsync(
            contractId, adjustmentId, input);
    }

    /// <summary>Post a durable monthly receivable; requires root, archived
    /// independent approval and a SERIALIZABLE isolated staging UoW.</summary>
    [HttpPost("contracts/{contractId:guid}/monthly-issued/{issuedId:guid}/post-receivable")]
    [UnitOfWork(true, System.Data.IsolationLevel.Serializable)]
    public Task<OfficialInvoicePostingRead> PostMonthlyReceivableAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid issuedId)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).PostOfficialReceivableAsync(
            contractId, issuedId, OfficialInvoiceKind.Monthly);
    }

    [HttpPost("contracts/{contractId:guid}/issued/{issuedId:guid}/post-receivable")]
    [UnitOfWork(true, System.Data.IsolationLevel.Serializable)]
    public Task<OfficialInvoicePostingRead> PostFinalReceivableAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid issuedId)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).PostOfficialReceivableAsync(
            contractId, issuedId, OfficialInvoiceKind.Final);
    }

    [HttpPost("contracts/{contractId:guid}/receivables/{postingId:guid}/allocations/{allocationId:guid}/review")]
    [UnitOfWork(true, System.Data.IsolationLevel.Serializable)]
    public Task<OfficialInvoiceReceiptReviewRead> ReviewOfficialReceiptAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid postingId,
        [FromRoute] Guid allocationId,
        [FromBody] OfficialInvoiceReceiptReviewInput input)
    {
        RequireIsolatedStaging();
        if (input is null) throw Denied();
        return (_mutation ?? throw Denied()).ReviewOfficialReceiptAsync(
            contractId, postingId, allocationId, input);
    }

    [HttpGet("contracts/{contractId:guid}/receivables/{postingId:guid}/balance")]
    [UnitOfWork(true, System.Data.IsolationLevel.RepeatableRead)]
    public Task<OfficialInvoiceReceivableBalance> GetOfficialBalanceAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid postingId,
        [FromQuery] DateOnly? asOfDate = null)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).GetOfficialBalanceAsync(
            contractId, postingId, asOfDate);
    }

    [HttpGet("contracts/{contractId:guid}/official-debt")]
    [UnitOfWork(true, System.Data.IsolationLevel.RepeatableRead)]
    public Task<OfficialContractDebtPortfolioRead> GetOfficialDebtPortfolioAsync(
        [FromRoute] Guid contractId, [FromQuery] DateOnly? asOfDate = null)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).GetOfficialDebtPortfolioAsync(
            contractId, asOfDate);
    }

    [HttpPost("contracts/{contractId:guid}/move-outs/{requestId:guid}/prepare")]
    [UnitOfWork(true)]
    public Task<SliceBStagedInvoiceEvidenceRead> PrepareAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied())
            .PrepareAsync(contractId, requestId);
    }

    [HttpPost("contracts/{contractId:guid}/evidence/{evidenceId:guid}/reviews")]
    [UnitOfWork(true)]
    public Task<Guid> SubmitReviewAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid evidenceId,
        [FromBody] SliceBInvoiceReviewSubmission input)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied())
            .SubmitIndependentReviewAsync(contractId, evidenceId,
                input?.Note);
    }

    [HttpPost("contracts/{contractId:guid}/reviews/{reviewId:guid}/decide")]
    [UnitOfWork(true)]
    public Task<Guid> DecideReviewAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid reviewId,
        [FromBody] SliceBInvoiceReviewDecision input)
    {
        RequireIsolatedStaging();
        if (input is null || string.IsNullOrWhiteSpace(input.Reason))
            throw Denied();
        return (_mutation ?? throw Denied())
            .RecordIndependentReviewAsync(
                contractId, reviewId, input.AcceptForDesign, input.Reason);
    }

    [HttpPost("contracts/{contractId:guid}/evidence/{evidenceId:guid}/issue-candidate")]
    [UnitOfWork(true)]
    public Task<SliceBStagedIssuedInvoiceRead> IssueCandidateAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid evidenceId,
        [FromBody] FinalInvoiceFinancialTermsProposal input)
    {
        RequireIsolatedStaging();
        if (input is null)
            throw Denied();
        return (_mutation ?? throw Denied())
            .IssueCandidateAsync(contractId, evidenceId, input);
    }

    [HttpPost("contracts/{contractId:guid}/issued/{issuedId:guid}/manual-receipts")]
    [UnitOfWork(true)]
    public Task<SliceBStagedInvoiceReceiptRead> RecordManualReceiptAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid issuedId,
        [FromBody] SliceBFinalInvoiceManualReceiptInput input)
    {
        RequireIsolatedStaging();
        if (input is null)
            throw Denied();
        return (_mutation ?? throw Denied())
            .RecordManualReceiptAsync(contractId, issuedId, input);
    }

    /// <summary>
    /// Phase 2G.4: approve refund/forfeiture amounts BEFORE final move-out.
    /// Does not execute a refund or offset debt. Root-only staging action.
    /// </summary>
    [HttpPost("contracts/{contractId:guid}/move-outs/{requestId:guid}/deposit-settlement/approve")]
    [UnitOfWork(true, System.Data.IsolationLevel.Serializable)]
    public Task<SliceBDepositSettlementDecisionRead> ApproveDepositSettlementAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId,
        [FromBody] SliceBDepositSettlementApprovalInput input)
    {
        RequireIsolatedStaging();
        if (input is null) throw Denied();
        return (_mutation ?? throw Denied()).ApproveDepositSettlementAsync(
            contractId, requestId, input);
    }

    [HttpGet("contracts/{contractId:guid}/move-outs/{requestId:guid}/deposit-settlement")]
    [UnitOfWork(true, System.Data.IsolationLevel.RepeatableRead)]
    public Task<SliceBDepositSettlementDecisionRead> GetDepositSettlementAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).GetDepositSettlementAsync(
            contractId, requestId);
    }

    [HttpPost("contracts/{contractId:guid}/move-outs/{requestId:guid}/complete")]
    [UnitOfWork(true)]
    public Task<SliceBStagedMoveOutCloseoutRead> CompleteMoveOutAsync(
        [FromRoute] Guid contractId, [FromRoute] Guid requestId,
        [FromBody] SliceBFinalMoveOutCloseoutInput input)
    {
        RequireIsolatedStaging();
        if (input is null)
            throw Denied();
        return (_mutation ?? throw Denied())
            .CompleteMoveOutAsync(contractId, requestId, input);
    }

    private void RequireIsolatedStaging()
    {
        if (!_environment.IsStaging() ||
            !_configuration.GetValue<bool>(
                "BuildingManagement:SliceAStaging:Enabled") ||
            _configuration["BuildingManagement:SliceAStaging:Activation"] !=
                "I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES" ||
            _mutation is null)
            throw Denied();

        var identity = _configuration.GetConnectionString("Default");
        var buildings = _configuration.GetConnectionString("BuildingManagement");
        if (string.IsNullOrWhiteSpace(identity) ||
            string.IsNullOrWhiteSpace(buildings))
            throw Denied();
        var identityDb = new NpgsqlConnectionStringBuilder(identity);
        var buildingsDb = new NpgsqlConnectionStringBuilder(buildings);
        if (!identityDb.Database.StartsWith(
                "bm_slicea_staging_identity_", StringComparison.OrdinalIgnoreCase) ||
            !buildingsDb.Database.StartsWith(
                "bm_slicea_staging_buildings_", StringComparison.OrdinalIgnoreCase) ||
            identityDb.Database == buildingsDb.Database)
            throw Denied();
    }

    private static AbpAuthorizationException Denied()
        => new("Final invoice mutation is disabled outside two isolated, " +
               "explicitly authorized staging databases.");
}

public sealed class SliceBInvoiceReviewSubmission
{
    public string? Note { get; set; }
}

public sealed class SliceBInvoiceReviewDecision
{
    public bool AcceptForDesign { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class SliceBMonthlyReviewInput
{
    public bool Accept { get; set; }
    public string Reason { get; set; } = string.Empty;
}
