using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
using BuildingManagement.Rooms;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;

namespace BuildingManagement.EntityFrameworkCore.Security;

/// <summary>
/// An internal, query-composition-only candidate store. Despite being DI
/// resolvable it is NOT an application service, remote endpoint, or
/// authorization decision. Caller MUST supply trusted and CURRENT grants.
/// No business AppService calls this until full authorization review.
/// </summary>
[ExposeServices(typeof(IBuildingScopedCollectionStore))]
public sealed class EfBuildingScopedCollectionStore :
    IBuildingScopedCollectionStore, ITransientDependency
{
    private readonly IRepository<Building, Guid> _buildings;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IRepository<Contract, Guid> _contracts;

    public EfBuildingScopedCollectionStore(
        IRepository<Building, Guid> buildings,
        IRepository<Floor, Guid> floors,
        IRepository<Room, Guid> rooms,
        IRepository<Contract, Guid> contracts)
    {
        _buildings = buildings;
        _floors = floors;
        _rooms = rooms;
        _contracts = contracts;
    }

    public async Task<BuildingScopedListPage> ReadPageAsync(
        BuildingScopedCollectionKind kind,
        IReadOnlyCollection<Guid> trustedBuildingIds,
        int skipCount,
        int maxResultCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trustedBuildingIds);
        if (skipCount < 0 || maxResultCount < 1 || maxResultCount > 100)
            throw new ArgumentOutOfRangeException(
                nameof(maxResultCount), "Paging requires skip >= 0 and 1 <= take <= 100.");

        // Scope first, count second, deterministic ordering and page last.
        var b = await _buildings.GetQueryableAsync();
        return kind switch
        {
            BuildingScopedCollectionKind.Building => await PageAsync(
                BuildingBusinessScopedCollectionQueries.Buildings(
                    b, trustedBuildingIds), skipCount, maxResultCount,
                cancellationToken),
            BuildingScopedCollectionKind.Floor => await PageAsync(
                BuildingBusinessScopedCollectionQueries.Floors(
                    await _floors.GetQueryableAsync(), b,
                    trustedBuildingIds), skipCount, maxResultCount,
                cancellationToken),
            BuildingScopedCollectionKind.Room => await PageAsync(
                BuildingBusinessScopedCollectionQueries.Rooms(
                    await _rooms.GetQueryableAsync(),
                    await _floors.GetQueryableAsync(),
                    b, trustedBuildingIds), skipCount, maxResultCount,
                cancellationToken),
            BuildingScopedCollectionKind.Contract => await PageAsync(
                BuildingBusinessScopedCollectionQueries.Contracts(
                    await _contracts.GetQueryableAsync(),
                    await _rooms.GetQueryableAsync(),
                    await _floors.GetQueryableAsync(),
                    b, trustedBuildingIds), skipCount, maxResultCount,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static async Task<BuildingScopedListPage> PageAsync<TEntity>(
        IQueryable<TEntity> scoped, int skip, int take,
        CancellationToken cancellationToken)
        where TEntity : Entity<Guid>
    {
        var count = await scoped.CountAsync(cancellationToken);
        var ids = await scoped
            .OrderBy(entity => entity.Id)
            .Skip(skip).Take(take)
            .Select(entity => entity.Id)
            .ToArrayAsync(cancellationToken);

        // Count and page are separate DB reads in this RFC; final service
        // must choose transaction isolation to prevent inconsistent snapshots.
        return new BuildingScopedListPage(count, ids);
    }
}
