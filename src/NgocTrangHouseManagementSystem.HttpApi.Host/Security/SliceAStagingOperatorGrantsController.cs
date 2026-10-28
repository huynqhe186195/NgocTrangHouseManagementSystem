using System;
using System.Threading.Tasks;
using BuildingManagement.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Volo.Abp.Authorization;
using Volo.Abp.Uow;

namespace NgocTrangHouseManagementSystem.Security;

/// <summary>
/// Explicit, authenticated HTTP workflow to delegate one building only.
/// An ordinary administrator never bypasses Assign/Revoke permission checks.
/// Endpoints remain inaccessible unless the server opted in to ISOLATED
/// staging at startup. No unaudited production delegation controller.
/// </summary>
[ApiController]
[Authorize]
[Route("api/slice-a-staging/operator-grants")]
public sealed class SliceAStagingOperatorGrantsController : ControllerBase
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly BuildingOperatorChangeCoordinator _changes;

    public SliceAStagingOperatorGrantsController(
        IHostEnvironment environment,
        IConfiguration configuration,
        BuildingOperatorChangeCoordinator changes)
    {
        _environment = environment;
        _configuration = configuration;
        _changes = changes;
    }

    [HttpPost]
    [UnitOfWork]
    public async Task<AssignOperatorGrantResult> AssignAsync(
        [FromBody] AssignOperatorGrantRequest input)
    {
        RequireIsolatedStaging();
        ArgumentNullException.ThrowIfNull(input);
        var id = await _changes.AssignAsync(
            input.BuildingId, input.OperatorUserId, input.ExpiresAtUtc);
        return new AssignOperatorGrantResult(id);
    }

    [HttpPost("revoke")]
    [UnitOfWork]
    public async Task RevokeAsync([FromBody] RevokeOperatorGrantRequest input)
    {
        RequireIsolatedStaging();
        ArgumentNullException.ThrowIfNull(input);
        await _changes.RevokeAsync(
            input.BuildingId, input.OperatorUserId, input.ExpectedRevision);
    }

    private void RequireIsolatedStaging()
    {
        if (!_environment.IsStaging() ||
            !_configuration.GetValue<bool>(
                "BuildingManagement:SliceAStaging:Enabled") ||
            _configuration["BuildingManagement:SliceAStaging:Activation"] !=
                "I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES")
            throw new AbpAuthorizationException(
                "Building delegation is disabled outside isolated Slice A staging.");
        // The host module also requires dedicated staging DB names and one
        // root account before it can register persistence adapters.
        // Coordinator then RECHECKS ABP issuer, active Identity accounts,
        // dedicated permission and issuer membership on requested Building.
    }
}

public sealed record AssignOperatorGrantRequest(
    Guid BuildingId, Guid OperatorUserId, DateTime? ExpiresAtUtc);
public sealed record AssignOperatorGrantResult(Guid GrantId);
public sealed record RevokeOperatorGrantRequest(
    Guid BuildingId, Guid OperatorUserId, int ExpectedRevision);
