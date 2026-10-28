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
