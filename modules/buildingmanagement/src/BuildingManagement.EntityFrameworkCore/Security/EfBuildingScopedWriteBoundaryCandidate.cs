using System;
using System.Threading.Tasks;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Authorization;
using Volo.Abp.Users;

namespace BuildingManagement.EntityFrameworkCore.Security;

/// <summary>
/// UNREGISTERED adapter. Only the authenticated ABP principal is trusted.
/// The application service must independently verify active account,
/// permission and resource scope before calling this adapter.
/// EF fence rejects unmapped grants, non-PostgreSQL providers and nested
/// independent EF transactions. Production registration remains DenyAll.
/// </summary>
public sealed class EfBuildingScopedWriteBoundaryCandidate
    : IBuildingScopedWriteBoundary
{
    private readonly DbContext _db;
    private readonly ICurrentUser _user;
    private readonly TimeProvider _clock;

    public EfBuildingScopedWriteBoundaryCandidate(
        DbContext db, ICurrentUser user, TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public Task<T> ExecuteBuildingAsync<T>(
        Guid serverResolvedBuildingId, Func<Task<T>> mutation,
        BuildingScopedMutationKind kind = BuildingScopedMutationKind.Building)
    {
        if (!Enum.IsDefined(kind) ||
            !_user.IsAuthenticated || !_user.Id.HasValue ||
            _user.Id.Value == Guid.Empty || serverResolvedBuildingId == Guid.Empty)
            throw new AbpAuthorizationException(
                "Only the authenticated principal may mutate a building.");

        return new EfBuildingScopedWriteFenceCandidate(_db, _clock)
            .ExecuteAsync(_user.Id.Value, serverResolvedBuildingId, mutation);
    }
}
