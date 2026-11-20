using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Security;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;
using Volo.Abp.Users;

namespace BuildingManagement.OperatorMemberships;

/// <summary>
/// Read-only operator-to-building membership roster. It intentionally does not
/// expose assign/revoke commands or the ABP Identity user-management surface.
/// </summary>
public class OperatorMembershipsAppService :
    ApplicationService,
    IOperatorMembershipsAppService
{
    private readonly IBuildingCrudAccessPolicy _access;
    private readonly IOperatorMembershipReadStore _store;
    private readonly TimeProvider _clock;
    private readonly ICurrentUser _currentUser;

    public OperatorMembershipsAppService(
        IBuildingCrudAccessPolicy access,
        IOperatorMembershipReadStore store,
        ICurrentUser currentUser,
        TimeProvider? clock = null)
    {
        _access = access;
        _store = store;
        _currentUser = currentUser;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<PagedResultDto<OperatorMembershipSummaryDto>> GetListAsync(
        Guid? buildingId = null,
        int skipCount = 0,
        int maxResultCount = 50)
    {
        if (skipCount < 0 || maxResultCount < 1 || maxResultCount > 100 ||
            buildingId == Guid.Empty)
            throw Denied();

        // This checks the authenticated server principal, the dedicated read
        // permission, active account and current membership before any grant
        // history query is allowed to run.
        var authorized = await _access.GetAuthorizedBuildingIdsAsync(
            BuildingBusinessCapability.ReadOperators);

        if (buildingId.HasValue && !authorized.Contains(buildingId.Value))
            throw Denied();

        if (!_currentUser.IsAuthenticated || !_currentUser.Id.HasValue ||
            _currentUser.Id.Value == Guid.Empty)
            throw Denied();

        var now = _clock.GetUtcNow().UtcDateTime;
        var page = await _store.ReadPageAsync(
            _currentUser.Id.Value,
            authorized,
            buildingId,
            now,
            skipCount,
            maxResultCount);

        return new PagedResultDto<OperatorMembershipSummaryDto>
        {
            TotalCount = page.TotalCount,
            Items = page.Items.Select(item => Map(item, now)).ToList()
        };
    }

    private static OperatorMembershipSummaryDto Map(
        OperatorMembershipReadModel item, DateTime now)
    {
        var status = item.RevokedAtUtc.HasValue
            ? OperatorMembershipStatus.Revoked
            : item.GrantedAtUtc > now
                ? OperatorMembershipStatus.Scheduled
                : item.ExpiresAtUtc.HasValue && item.ExpiresAtUtc.Value <= now
                    ? OperatorMembershipStatus.Expired
                    : OperatorMembershipStatus.Active;

        return new OperatorMembershipSummaryDto
        {
            Id = item.GrantId,
            OperatorUserId = item.OperatorUserId,
            BuildingId = item.BuildingId,
            BuildingName = item.BuildingName,
            GrantedByUserId = item.GrantedByUserId,
            GrantedAtUtc = item.GrantedAtUtc,
            ExpiresAtUtc = item.ExpiresAtUtc,
            RevokedAtUtc = item.RevokedAtUtc,
            RevisionNumber = item.RevisionNumber,
            Status = status
        };
    }

    private static Volo.Abp.Authorization.AbpAuthorizationException Denied()
        => new("BuildingManagement: operator membership is not authorized.");
}
