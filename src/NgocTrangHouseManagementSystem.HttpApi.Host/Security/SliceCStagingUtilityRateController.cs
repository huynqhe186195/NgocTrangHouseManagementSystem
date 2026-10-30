using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BuildingManagement.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Volo.Abp.Authorization;
using Volo.Abp.Uow;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// This route is available ONLY on a deliberately configured isolated
/// staging host with two dedicated disposable PostgreSQL databases.
/// It never unlocks legacy global UtilityRateAppService HTTP routes.
/// </summary>
[ApiController]
[Authorize]
[Route("api/slice-c-staging/utility-rates")]
public sealed class SliceCStagingUtilityRateController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly SliceCStagingUtilityRates? _rates;

    public SliceCStagingUtilityRateController(
        IHostEnvironment environment, IConfiguration configuration,
        SliceCStagingUtilityRates? rates = null)
    {
        _environment = environment;
        _configuration = configuration;
        _rates = rates;
    }

    [HttpGet]
    [UnitOfWork(true)]
    public Task<IReadOnlyList<SliceCUtilityRateRead>> GetListAsync(
        [FromQuery] int skipCount = 0, [FromQuery] int maxResultCount = 50)
    {
        RequireStaging();
        return _rates!.GetListAsync(skipCount, maxResultCount);
    }

    [HttpGet("{id:guid}")]
    [UnitOfWork(true)]
    public Task<SliceCUtilityRateRead> GetAsync([FromRoute] Guid id)
    {
        RequireStaging();
        return _rates!.GetAsync(id);
    }

    [HttpPost]
    [UnitOfWork(true)]
    public Task<SliceCUtilityRateRead> CreateAsync(
        [FromBody] CreateUtilityRateDto input)
    {
        RequireStaging();
        return _rates!.CreateAsync(input);
    }

    [HttpPut("{id:guid}")]
    [UnitOfWork(true)]
    public Task<SliceCUtilityRateRead> UpdateAsync(
        [FromRoute] Guid id, [FromBody] UpdateUtilityRateDto input)
    {
        RequireStaging();
        return _rates!.UpdateAsync(id, input);
    }

    [HttpDelete("{id:guid}")]
    [UnitOfWork(true)]
    public Task<bool> DeleteAsync([FromRoute] Guid id)
    {
        RequireStaging();
        return _rates!.DeleteAsync(id);
    }

    private void RequireStaging()
    {
        if (!_environment.IsStaging() ||
            !_configuration.GetValue<bool>("BuildingManagement:SliceAStaging:Enabled") ||
            _configuration["BuildingManagement:SliceAStaging:Activation"] !=
                "I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES" ||
            _rates is null)
            throw Denied();
        var id = _configuration.GetConnectionString("Default");
        var buildings = _configuration.GetConnectionString("BuildingManagement");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(buildings))
            throw Denied();
        var identity = new NpgsqlConnectionStringBuilder(id);
        var ledger = new NpgsqlConnectionStringBuilder(buildings);
        if (!identity.Database.StartsWith("bm_slicea_staging_identity_",
                StringComparison.OrdinalIgnoreCase) ||
            !ledger.Database.StartsWith("bm_slicea_staging_buildings_",
                StringComparison.OrdinalIgnoreCase) ||
            identity.Database == ledger.Database)
            throw Denied();
    }

    private static AbpAuthorizationException Denied()
        => new("Utility tariff operations require isolated staging authorization.");
}
