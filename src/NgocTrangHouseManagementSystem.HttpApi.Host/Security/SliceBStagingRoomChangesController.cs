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
/// Explicit request/review-only API. No Approve, Complete, Transfer,
/// Refund, CarryForward or ChangeRoom route is present.
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

    [HttpPost]
    [UnitOfWork(true, IsolationLevel.Serializable)]
    public Task<SliceBRoomChangeRequestRead> SubmitAsync(
        [FromRoute] Guid contractId,
        [FromBody] SliceBRoomChangeSubmitInput input)
    {
        RequireIsolatedStaging();
        return _requests!.SubmitAsync(contractId, input);
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
