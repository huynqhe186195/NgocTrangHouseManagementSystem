using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.RoomReservations;
using BuildingManagement.Payments;
using BuildingManagement.MoveOuts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Volo.Abp.Authorization;
using Volo.Abp.Uow;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Isolated-staging-only Slice B contract entrypoint. This does NOT
/// enable the legacy unscoped contract controller or arbitrary mutations.
/// Only create/update Draft contracts pass the separately fenced adapter.
/// </summary>
[ApiController]
[Authorize]
[Route("api/slice-b-staging/contracts")]
public sealed class SliceBStagingContractsController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly SliceBStagingContractQuery? _query;
    private readonly SliceBStagingContractMutation? _mutation;
    private readonly SliceBStagingReservationQuery? _reservationQuery;

    public SliceBStagingContractsController(
        IHostEnvironment environment, IConfiguration configuration,
        SliceBStagingContractQuery? query = null,
        SliceBStagingContractMutation? mutation = null,
        SliceBStagingReservationQuery? reservationQuery = null)
    {
        _environment = environment;
        _configuration = configuration;
        _query = query;
        _mutation = mutation;
        _reservationQuery = reservationQuery;
    }

    [HttpGet]
    [UnitOfWork(true)]
    public Task<IReadOnlyList<SliceBScopedContractRead>> GetListAsync(
        [FromQuery] int skipCount = 0, [FromQuery] int maxResultCount = 50)
    {
        RequireIsolatedStaging();
        return _query!.GetListAsync(skipCount, maxResultCount);
    }

    [HttpGet("{id:guid}")]
    [UnitOfWork(true)]
    public Task<SliceBScopedContractRead> GetAsync([FromRoute] Guid id)
    {
        RequireIsolatedStaging();
        return _query!.GetAsync(id);
    }

    [HttpPost]
    [UnitOfWork(true)]
    public Task<ContractDto> CreateAsync([FromBody] CreateContractDto input)
    {
        RequireIsolatedStaging();
        if (_mutation is null)
            throw Denied();
        return _mutation.CreateAsync(input);
    }

    [HttpPut("{id:guid}")]
    [UnitOfWork(true)]
    public Task<ContractDto> UpdateAsync(
        [FromRoute] Guid id, [FromBody] UpdateContractDto input)
    {
        RequireIsolatedStaging();
        if (_mutation is null)
            throw Denied();
        return _mutation.UpdateAsync(id, input);
    }

    // Cancelling an unsigned contract does not end an Active tenancy,
    // release a room, or settle a deposit.
    [HttpPost("{id:guid}/cancel")]
    [UnitOfWork(true)]
    public Task<ContractDto> CancelDraftContractAsync([FromRoute] Guid id)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).CancelDraftContractAsync(id);
    }

    [HttpPost("{id:guid}/sign")]
    [UnitOfWork(true)]
    public Task<ContractDto> SignAsync([FromRoute] Guid id)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).SignAsync(id);
    }

    [HttpPost("{id:guid}/activate")]
    [UnitOfWork(true)]
    public Task<ContractDto> ActivateAsync([FromRoute] Guid id)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).ActivateAsync(id);
    }

    // Reservation read is intentionally separate from the legacy unscoped
    // RoomReservationAppService and projects no global Tenant profile PII.
    [HttpGet("reservations")]
    [UnitOfWork(true)]
    public Task<IReadOnlyList<SliceBScopedReservationRead>> GetRoomReservationsAsync(
        [FromQuery] int skipCount = 0, [FromQuery] int maxResultCount = 50)
    {
        RequireIsolatedStaging();
        return (_reservationQuery ?? throw Denied())
            .GetListAsync(skipCount, maxResultCount);
    }

    [HttpGet("reservations/{id:guid}")]
    [UnitOfWork(true)]
    public Task<SliceBScopedReservationRead> GetRoomReservationAsync(
        [FromRoute] Guid id)
    {
        RequireIsolatedStaging();
        return (_reservationQuery ?? throw Denied()).GetAsync(id);
    }

    [HttpPost("reservations")]
    [UnitOfWork(true)]
    public Task<RoomReservationDto> CreateRoomReservationAsync(
        [FromBody] CreateRoomReservationDto input)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).CreateRoomReservationAsync(input);
    }

    // Only the scoped reservation can be cancelled. The domain service
    // atomically applies its existing deposit disposition on paid holds.
    [HttpPost("reservations/{id:guid}/cancel")]
    [UnitOfWork(true)]
    public Task<RoomReservationDto> CancelRoomReservationAsync(
        [FromRoute] Guid id, [FromBody] CancelRoomReservationDto input)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).CancelRoomReservationAsync(id, input);
    }

    // Records a pending deposit payment only; does NOT complete a transfer.
    [HttpGet("reservations/{id:guid}/deposit-payments")]
    [UnitOfWork(true)]
    public Task<IReadOnlyList<SliceBScopedDepositPaymentRead>>
        GetReservationDepositPaymentsAsync([FromRoute] Guid id)
    {
        RequireIsolatedStaging();
        return (_reservationQuery ?? throw Denied())
            .GetReservationDepositPaymentsAsync(id);
    }

    [HttpPost("reservations/{id:guid}/deposit-payments")]
    [UnitOfWork(true)]
    public Task<PaymentDto> CreateReservationDepositPaymentAsync(
        [FromRoute] Guid id, [FromBody] CreateReservationDepositPaymentDto input)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied())
            .CreateReservationDepositPaymentAsync(id, input);
    }

    [HttpPost("reservations/{id:guid}/deposit-payments/{paymentId:guid}/cancel")]
    [UnitOfWork(true)]
    public Task<PaymentDto> CancelReservationDepositPaymentAsync(
        [FromRoute] Guid id, [FromRoute] Guid paymentId)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied())
            .CancelReservationDepositPaymentAsync(id, paymentId);
    }

    // Completion is a manually confirmed deposit receipt under the
    // original payment service's Contract→Reservation→Payment lock order.
    [HttpPost("reservations/{reservationId:guid}/deposit-payments/{paymentId:guid}/complete")]
    [UnitOfWork(true)]
    public Task<PaymentDto> CompleteReservationDepositPaymentAsync(
        [FromRoute] Guid reservationId, [FromRoute] Guid paymentId,
        [FromBody] CompletePaymentDto input)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied())
            .CompleteReservationDepositPaymentAsync(reservationId, paymentId, input);
    }

    // Accounting allocation only; no new payment or refund.
    [HttpPost("{id:guid}/allocate-reservation/{reservationId:guid}")]
    [UnitOfWork(true)]
    public Task<ContractDepositAllocationDto> AllocateReservationDepositAsync(
        [FromRoute] Guid id, [FromRoute] Guid reservationId)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied())
            .AllocateReservationDepositAsync(id, reservationId);
    }

    // Staging scoped ContractDeposit receipt; the original payment service
    // validates cash receipt and records an allocation in the same UoW.
    [HttpGet("{id:guid}/deposit-payments")]
    [UnitOfWork(true)]
    public Task<IReadOnlyList<SliceBScopedDepositPaymentRead>>
        GetContractDepositPaymentsAsync([FromRoute] Guid id)
    {
        RequireIsolatedStaging();
        return (_query ?? throw Denied()).GetContractDepositPaymentsAsync(id);
    }

    [HttpPost("{id:guid}/deposit-payments")]
    [UnitOfWork(true)]
    public Task<PaymentDto> CreateContractDepositPaymentAsync(
        [FromRoute] Guid id, [FromBody] CreateContractDepositPaymentDto input)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied())
            .CreateContractDepositPaymentAsync(id, input);
    }

    [HttpPost("{id:guid}/deposit-payments/{paymentId:guid}/complete")]
    [UnitOfWork(true)]
    public Task<PaymentDto> CompleteContractDepositPaymentAsync(
        [FromRoute] Guid id, [FromRoute] Guid paymentId,
        [FromBody] CompletePaymentDto input)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied())
            .CompleteContractDepositPaymentAsync(id, paymentId, input);
    }

    [HttpPost("{id:guid}/deposit-payments/{paymentId:guid}/cancel")]
    [UnitOfWork(true)]
    public Task<PaymentDto> CancelContractDepositPaymentAsync(
        [FromRoute] Guid id, [FromRoute] Guid paymentId)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied())
            .CancelContractDepositPaymentAsync(id, paymentId);
    }

    // Move-out scheduling/approval only; never completes move-out, settles
    // deposits, ends a contract, or releases an occupied room.
    [HttpGet("{id:guid}/move-out-requests")]
    [UnitOfWork(true)]
    public Task<IReadOnlyList<SliceBScopedMoveOutRequestRead>> GetMoveOutRequestsAsync(
        [FromRoute] Guid id)
    {
        RequireIsolatedStaging();
        return (_query ?? throw Denied()).GetMoveOutRequestsAsync(id);
    }

    [HttpPost("{id:guid}/move-out-requests")]
    [UnitOfWork(true)]
    public Task<MoveOutRequestDto> CreateMoveOutRequestAsync(
        [FromRoute] Guid id, [FromBody] CreateMoveOutRequestDto input)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).CreateMoveOutRequestAsync(id, input);
    }

    [HttpPost("{id:guid}/move-out-requests/{requestId:guid}/approve")]
    [UnitOfWork(true)]
    public Task<MoveOutRequestDto> ApproveMoveOutRequestAsync(
        [FromRoute] Guid id, [FromRoute] Guid requestId,
        [FromBody] ApproveMoveOutRequestDto input)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).ApproveMoveOutRequestAsync(
            id, requestId, input);
    }

    [HttpGet("{id:guid}/move-out-requests/{requestId:guid}/readiness")]
    [UnitOfWork(true)]
    public Task<MoveOutCompletionReadinessDto> GetMoveOutReadinessAsync(
        [FromRoute] Guid id, [FromRoute] Guid requestId)
    {
        RequireIsolatedStaging();
        return (_query ?? throw Denied()).GetMoveOutReadinessAsync(
            id, requestId);
    }

    [HttpGet("{id:guid}/move-out-requests/{requestId:guid}/financial-readiness")]
    [UnitOfWork(true)]
    public Task<SliceBScopedMoveOutFinancialRead> GetMoveOutFinancialReadinessAsync(
        [FromRoute] Guid id, [FromRoute] Guid requestId)
    {
        RequireIsolatedStaging();
        return (_query ?? throw Denied()).GetMoveOutFinancialReadinessAsync(
            id, requestId);
    }

    // Historical snapshot read only; never exposes drafts from a foreign
    // contract or building and never treats provisional prices as debt.
    [HttpGet("{id:guid}/monthly-utility-drafts/{draftId:guid}")]
    [UnitOfWork(true)]
    public Task<BuildingManagement.Invoices.InvoiceDraftDto>
        GetMonthlyUtilityDraftAsync(
            [FromRoute] Guid id, [FromRoute] Guid draftId)
    {
        RequireIsolatedStaging();
        return (_query ?? throw Denied())
            .GetMonthlyUtilityDraftAsync(id, draftId);
    }

    // Draft-only utility persistence. Do NOT accept client-supplied prices,
    // meter evidence, draft totals, or an unscoped ContractId.
    [HttpPost("{id:guid}/monthly-utility-drafts/{year:int}/{month:int}")]
    [UnitOfWork(true, System.Data.IsolationLevel.RepeatableRead)]
    public Task<BuildingManagement.Invoices.InvoiceDraftDto>
        CreateMonthlyUtilityDraftAsync(
            [FromRoute] Guid id, [FromRoute] int year, [FromRoute] int month)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied())
            .CreateMonthlyUtilityDraftAsync(id, year, month);
    }

    // Full-month provisional rent and utilities; service fees require
    // independent policy review. Never issues or posts an Invoice.
    [HttpGet("{id:guid}/monthly-invoice-preview/{year:int}/{month:int}")]
    [UnitOfWork(true, System.Data.IsolationLevel.RepeatableRead)]
    public Task<BuildingManagement.Invoices.MonthlyInvoiceBasePreviewDto>
        GetMonthlyInvoicePreviewAsync(
        [FromRoute] Guid id, [FromRoute] int year, [FromRoute] int month)
    {
        RequireIsolatedStaging();
        return (_query ?? throw Denied())
            .GetMonthlyInvoicePreviewAsync(id, year, month);
    }

    // Phase 2G.3: complete contract-level observed invoice/debt portfolio.
    // All amounts come from immutable issued evidence and journaled receipts.
    [HttpGet("{id:guid}/issued-invoices/debt")]
    [UnitOfWork(true, System.Data.IsolationLevel.RepeatableRead)]
    public Task<SliceBScopedContractIssuedDebtRead> GetIssuedDebtPortfolioAsync(
        [FromRoute] Guid id, [FromQuery] DateOnly? asOfDate = null)
    {
        RequireIsolatedStaging();
        return (_query ?? throw Denied())
            .GetIssuedDebtPortfolioAsync(id, asOfDate);
    }

    // Phase 2G.3: observed issued-invoice balance only, verified against
    // immutable source ledger and completed payment allocation evidence.
    // It never posts debt, applies deposits, or verifies external banks.
    [HttpGet("{id:guid}/issued-invoices/{issuedId:guid}/balance")]
    [UnitOfWork(true, System.Data.IsolationLevel.RepeatableRead)]
    public Task<SliceBScopedIssuedInvoiceBalanceRead> GetIssuedInvoiceBalanceAsync(
        [FromRoute] Guid id, [FromRoute] Guid issuedId)
    {
        RequireIsolatedStaging();
        return (_query ?? throw Denied())
            .GetIssuedInvoiceBalanceAsync(id, issuedId);
    }

    // A durable signed closeout is read from the immutable ledger, not
    // inferred from preflight flags. No bank/tenant personal data exposed.
    [HttpGet("{id:guid}/move-out-requests/{requestId:guid}/closeout")]
    [UnitOfWork(true)]
    public Task<SliceBScopedSignedCloseoutRead> GetSignedCloseoutAsync(
        [FromRoute] Guid id, [FromRoute] Guid requestId)
    {
        RequireIsolatedStaging();
        return (_query ?? throw Denied()).GetSignedCloseoutAsync(id, requestId);
    }

    [HttpPost("{id:guid}/move-out-requests/{requestId:guid}/verify-final-meters")]
    [UnitOfWork(true)]
    public Task<MoveOutChecklistTaskDto> VerifyFinalMetersAsync(
        [FromRoute] Guid id, [FromRoute] Guid requestId)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).VerifyFinalMetersAsync(id, requestId);
    }

    [HttpPost("{id:guid}/move-out-requests/{requestId:guid}/reject")]
    [UnitOfWork(true)]
    public Task<MoveOutRequestDto> RejectMoveOutRequestAsync(
        [FromRoute] Guid id, [FromRoute] Guid requestId,
        [FromBody] RejectMoveOutRequestDto input)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).RejectMoveOutRequestAsync(
            id, requestId, input);
    }

    [HttpPost("{id:guid}/move-out-requests/{requestId:guid}/cancel")]
    [UnitOfWork(true)]
    public Task<MoveOutRequestDto> CancelMoveOutRequestAsync(
        [FromRoute] Guid id, [FromRoute] Guid requestId)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).CancelMoveOutRequestAsync(
            id, requestId);
    }

    [HttpGet("{id:guid}/move-out-requests/{requestId:guid}/checklist")]
    [UnitOfWork(true)]
    public Task<IReadOnlyList<SliceBScopedMoveOutChecklistRead>> GetMoveOutChecklistAsync(
        [FromRoute] Guid id, [FromRoute] Guid requestId)
    {
        RequireIsolatedStaging();
        return (_query ?? throw Denied()).GetMoveOutChecklistAsync(id, requestId);
    }

    [HttpPost("{id:guid}/renewal-holds")]
    [UnitOfWork(true)]
    public Task<ContractRenewalHoldDto> CreateRenewalHoldAsync(
        [FromRoute] Guid id, [FromBody] SliceBCreateRenewalHoldInput input)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).CreateRenewalHoldAsync(
            id, input?.Notes);
    }

    [HttpPost("renewal-holds/{holdId:guid}/cancel")]
    [UnitOfWork(true)]
    public Task<ContractRenewalHoldDto> CancelRenewalHoldAsync(
        [FromRoute] Guid holdId)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).CancelRenewalHoldAsync(holdId);
    }

    [HttpPost("{id:guid}/renew")]
    [UnitOfWork(true)]
    public Task<ContractDto> RenewAsync(
        [FromRoute] Guid id, [FromBody] RenewContractDto input)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).RenewAsync(id, input);
    }

    [HttpPost("{id:guid}/tenants/new")]
    [UnitOfWork(true)]
    public Task<SliceBScopedContractTenantLink> RegisterTenantAsync(
        [FromRoute] Guid id, [FromBody] SliceBNewContractTenantInput input)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).RegisterTenantAsync(id, input);
    }

    [HttpGet("{id:guid}/tenants")]
    [UnitOfWork(true)]
    public Task<IReadOnlyList<SliceBScopedContractTenantLink>> GetTenantsAsync(
        [FromRoute] Guid id)
    {
        RequireIsolatedStaging();
        return _query!.GetTenantsAsync(id);
    }

    [HttpPost("{id:guid}/tenants")]
    [UnitOfWork(true)]
    public Task<ContractTenantDto> AddTenantAsync(
        [FromRoute] Guid id, [FromBody] AddContractTenantDto input)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).AddTenantAsync(id, input);
    }

    [HttpPut("{id:guid}/tenants/{tenantId:guid}")]
    [UnitOfWork(true)]
    public Task<ContractTenantDto> UpdateTenantRoleAsync(
        [FromRoute] Guid id, [FromRoute] Guid tenantId,
        [FromBody] UpdateContractTenantRoleDto input)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).UpdateTenantRoleAsync(id, tenantId, input);
    }

    [HttpDelete("{id:guid}/tenants/{tenantId:guid}")]
    [UnitOfWork(true)]
    public Task<bool> RemoveTenantAsync(
        [FromRoute] Guid id, [FromRoute] Guid tenantId)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied()).RemoveTenantAsync(id, tenantId);
    }

    private void RequireIsolatedStaging()
    {
        if (!_environment.IsStaging() ||
            !_configuration.GetValue<bool>(
                "BuildingManagement:SliceAStaging:Enabled") ||
            _configuration["BuildingManagement:SliceAStaging:Activation"] !=
                "I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES" ||
            _query is null)
            throw Denied();

        // Independent check of BOTH database names, not just the flag.
        var identity = new NpgsqlConnectionStringBuilder(
            _configuration.GetConnectionString("Default"));
        var buildings = new NpgsqlConnectionStringBuilder(
            _configuration.GetConnectionString("BuildingManagement"));
        if (!identity.Database.StartsWith(
                "bm_slicea_staging_identity_", StringComparison.OrdinalIgnoreCase) ||
            !buildings.Database.StartsWith(
                "bm_slicea_staging_buildings_", StringComparison.OrdinalIgnoreCase) ||
            identity.Database == buildings.Database)
            throw Denied();
    }

    private static AbpAuthorizationException Denied()
        => new("Slice B contract reads require isolated staging authorization.");
}

public sealed class SliceBCreateRenewalHoldInput
{
    public string? Notes { get; set; }
}
