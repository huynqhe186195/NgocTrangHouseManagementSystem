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
    [HttpPost("reservations/{id:guid}/deposit-payments")]
    [UnitOfWork(true)]
    public Task<PaymentDto> CreateReservationDepositPaymentAsync(
        [FromRoute] Guid id, [FromBody] CreateReservationDepositPaymentDto input)
    {
        RequireIsolatedStaging();
        return (_mutation ?? throw Denied())
            .CreateReservationDepositPaymentAsync(id, input);
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
