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
/// Only an isolated two-database staging host enables the scoped reading
/// endpoints. The conventional global MeterReadingAppService remains denied.
/// </summary>
[ApiController]
[Authorize]
[Route("api/slice-c-staging/meter-readings")]
public sealed class SliceCStagingMeterReadingController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly SliceCStagingMeterReadings? _readings;

    public SliceCStagingMeterReadingController(
        IHostEnvironment environment, IConfiguration configuration,
        SliceCStagingMeterReadings? readings = null)
    {
        _environment = environment;
        _configuration = configuration;
        _readings = readings;
    }

    [HttpGet]
    [UnitOfWork(true)]
    public Task<IReadOnlyList<SliceCMeterReadingRead>> GetListAsync(
        [FromQuery] int skipCount = 0, [FromQuery] int maxResultCount = 50)
    {
        RequireStaging();
        return _readings!.GetListAsync(skipCount, maxResultCount);
    }

    [HttpGet("{id:guid}")]
    [UnitOfWork(true)]
    public Task<SliceCMeterReadingRead> GetAsync([FromRoute] Guid id)
    {
        RequireStaging();
        return _readings!.GetAsync(id);
    }

    [HttpPost]
    [UnitOfWork(true)]
    public Task<SliceCMeterReadingRead> CreateAsync(
        [FromBody] CreateMeterReadingDto input)
    {
        RequireStaging();
        return _readings!.CreateAsync(input);
    }

    [HttpPut("{id:guid}")]
    [UnitOfWork(true)]
    public Task<SliceCMeterReadingRead> UpdateAsync(
        [FromRoute] Guid id, [FromBody] UpdateMeterReadingDto input)
    {
        RequireStaging();
        return _readings!.UpdateAsync(id, input);
    }

    private void RequireStaging()
    {
        if (!_environment.IsStaging() ||
            !_configuration.GetValue<bool>("BuildingManagement:SliceAStaging:Enabled") ||
            _configuration["BuildingManagement:SliceAStaging:Activation"] !=
                "I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES" ||
            _readings is null)
            throw Denied();
        var identityCs = _configuration.GetConnectionString("Default");
        var buildingCs = _configuration.GetConnectionString("BuildingManagement");
        if (string.IsNullOrWhiteSpace(identityCs) ||
            string.IsNullOrWhiteSpace(buildingCs))
            throw Denied();
        var identity = new NpgsqlConnectionStringBuilder(identityCs);
        var building = new NpgsqlConnectionStringBuilder(buildingCs);
        if (!identity.Database.StartsWith("bm_slicea_staging_identity_",
                StringComparison.OrdinalIgnoreCase) ||
            !building.Database.StartsWith("bm_slicea_staging_buildings_",
                StringComparison.OrdinalIgnoreCase) ||
            identity.Database == building.Database)
            throw Denied();
    }

    private static AbpAuthorizationException Denied()
        => new("Meter reading operations require isolated staging authorization.");
}
