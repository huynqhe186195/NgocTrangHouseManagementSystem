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
/// Root-owner-only first implementation of room-specific service tariff
/// pricing, isolated PostgreSQL staging only. This does NOT issue an
/// invoice or expose tenant data; per-building Owner authorization
/// requires a separate reviewed role/membership implementation.
/// </summary>
[ApiController]
[Authorize]
[Route("api/slice-b-staging/rooms/{roomId:guid}/service-tariffs")]
public sealed class SliceBStagingRoomScopedServiceTariffsController
    : ControllerBase
{
    private readonly IHostEnvironment _host;
    private readonly IConfiguration _configuration;
    private readonly SliceBStagingRoomScopedServiceTariffs? _tariffs;

    public SliceBStagingRoomScopedServiceTariffsController(
        IHostEnvironment host, IConfiguration configuration,
        SliceBStagingRoomScopedServiceTariffs? tariffs = null)
    {
        _host = host;
        _configuration = configuration;
        _tariffs = tariffs;
    }

    [HttpGet]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomScopedServiceTariffRead[]> GetListAsync(
        [FromRoute] Guid roomId, [FromQuery] Guid serviceId,
        [FromQuery] int skipCount = 0, [FromQuery] int maxResultCount = 50)
    {
        RequireIsolatedStaging();
        return _tariffs!.GetListAsync(
            roomId, serviceId, skipCount, maxResultCount);
    }

    [HttpPost]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomScopedServiceTariffRead> AppendAsync(
        [FromRoute] Guid roomId,
        [FromBody] SliceBRoomScopedServiceTariffInput input)
    {
        RequireIsolatedStaging();
        return _tariffs!.AppendAsync(roomId, input);
    }

    private void RequireIsolatedStaging()
    {
        if (!_host.IsStaging() ||
            !_configuration.GetValue<bool>(
                "BuildingManagement:SliceAStaging:Enabled") ||
            _configuration["BuildingManagement:SliceAStaging:Activation"] !=
                "I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES" ||
            _tariffs is null)
            throw Denied();

        var identity = new NpgsqlConnectionStringBuilder(
            _configuration.GetConnectionString("Default"));
        var building = new NpgsqlConnectionStringBuilder(
            _configuration.GetConnectionString("BuildingManagement"));
        if (!identity.Database.StartsWith("bm_slicea_staging_identity_",
                StringComparison.OrdinalIgnoreCase) ||
            !building.Database.StartsWith("bm_slicea_staging_buildings_",
                StringComparison.OrdinalIgnoreCase) ||
            identity.Database == building.Database)
            throw Denied();
    }

    private static AbpAuthorizationException Denied()
        => new("Room service tariff edit is disabled outside isolated staging.");
}
