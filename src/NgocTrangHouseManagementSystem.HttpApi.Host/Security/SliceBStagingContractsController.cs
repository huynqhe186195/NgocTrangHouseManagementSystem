using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
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

    public SliceBStagingContractsController(
        IHostEnvironment environment, IConfiguration configuration,
        SliceBStagingContractQuery? query = null,
        SliceBStagingContractMutation? mutation = null)
    {
        _environment = environment;
        _configuration = configuration;
        _query = query;
        _mutation = mutation;
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
