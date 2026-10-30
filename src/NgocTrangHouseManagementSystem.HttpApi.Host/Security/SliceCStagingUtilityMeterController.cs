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

/// <summary>All nine physical meter operations, staging and two DBs only.</summary>
[ApiController]
[Authorize]
[Route("api/slice-c-staging/utility-meters")]
public sealed class SliceCStagingUtilityMeterController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly SliceCStagingUtilityMeters? _meters;

    public SliceCStagingUtilityMeterController(
        IHostEnvironment environment, IConfiguration configuration,
        SliceCStagingUtilityMeters? meters = null)
    {
        _environment = environment;
        _configuration = configuration;
        _meters = meters;
    }

    [HttpGet]
    [UnitOfWork(true)]
    public Task<IReadOnlyList<SliceCUtilityMeterRead>> GetListAsync(
        [FromQuery] int skipCount = 0, [FromQuery] int maxResultCount = 50)
    {
        RequireStaging();
        return _meters!.GetListAsync(skipCount, maxResultCount);
    }

    [HttpGet("{id:guid}")]
    [UnitOfWork(true)]
    public Task<SliceCUtilityMeterRead> GetAsync([FromRoute] Guid id)
    {
        RequireStaging();
        return _meters!.GetAsync(id);
    }

    [HttpPost]
    [UnitOfWork(true)]
    public Task<SliceCUtilityMeterRead> CreateAsync(
        [FromBody] CreateUtilityMeterDto input)
    {
        RequireStaging();
        return _meters!.CreateAsync(input);
    }

    [HttpPut("{id:guid}")]
    [UnitOfWork(true)]
    public Task<SliceCUtilityMeterRead> UpdateAsync(
        [FromRoute] Guid id, [FromBody] UpdateUtilityMeterDto input)
    {
        RequireStaging();
        return _meters!.UpdateAsync(id, input);
    }

    [HttpPost("{id:guid}/replace")]
    [UnitOfWork(true)]
    public Task<SliceCMeterReplacementRead> ReplaceAsync(
        [FromRoute] Guid id, [FromBody] ReplaceUtilityMeterDto input)
    {
        RequireStaging();
        return _meters!.ReplaceAsync(id, input);
    }

    [HttpGet("replacements/{id:guid}")]
    [UnitOfWork(true)]
    public Task<SliceCMeterReplacementRead> GetReplacementAsync(
        [FromRoute] Guid id)
    {
        RequireStaging();
        return _meters!.GetReplacementAsync(id);
    }

    [HttpGet("rooms/{roomId:guid}/replacements")]
    [UnitOfWork(true)]
    public Task<IReadOnlyList<SliceCMeterReplacementRead>>
        GetReplacementsByRoomAsync([FromRoute] Guid roomId)
    {
        RequireStaging();
        return _meters!.GetReplacementsByRoomAsync(roomId);
    }

    [HttpPost("{id:guid}/deactivate")]
    [UnitOfWork(true)]
    public Task<SliceCUtilityMeterRead> DeactivateAsync([FromRoute] Guid id)
    {
        RequireStaging();
        return _meters!.DeactivateAsync(id);
    }

    [HttpDelete("{id:guid}")]
    [UnitOfWork(true)]
    public Task<bool> DeleteAsync([FromRoute] Guid id)
    {
        RequireStaging();
        return _meters!.DeleteAsync(id);
    }

    private void RequireStaging()
    {
        if (!_environment.IsStaging() ||
            !_configuration.GetValue<bool>("BuildingManagement:SliceAStaging:Enabled") ||
            _configuration["BuildingManagement:SliceAStaging:Activation"] !=
                "I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES" ||
            _meters is null)
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
        => new("Physical meter operations require isolated staging authorization.");
}
