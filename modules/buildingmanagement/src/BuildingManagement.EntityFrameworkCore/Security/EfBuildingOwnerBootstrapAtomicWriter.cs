using System;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Security;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Authorization;

namespace BuildingManagement.EntityFrameworkCore.Security;

/// <summary>
/// UNREGISTERED migration candidate for first-owner persistence. This class
/// deliberately has no ITransientDependency/ExposeServices marker, and the
/// running BuildingManagementDbContext DOES NOT map the membership entity.
/// It can only be exercised against a purpose-built test DbContext until
/// migration/rollback and the first-owner privilege authority are reviewed.
///
/// The two inserts are in ONE explicit transaction. If grant insertion fails
/// after the building row is written, the building insert is rolled back.
/// </summary>
public sealed class EfBuildingOwnerBootstrapAtomicWriter :
    IBuildingOwnerBootstrapWriter
{
    private readonly DbContext _db;

    public EfBuildingOwnerBootstrapAtomicWriter(DbContext db)
    {
        _db = db;
    }

    public async Task<Building> CreateWithOwnerAtomicallyAsync(
        Guid trustedOwnerUserId,
        string name,
        string address,
        string? description)
    {
        if (trustedOwnerUserId == Guid.Empty)
            throw new AbpAuthorizationException(
                "A verified server identity is required for first owner.");

        // Requires relational provider with explicit transactions.
        // Never use in memory/InMemory provider as proof of atomicity.
        // In a real ABP ApplicationService, its UoW may already own the
        // Building DB transaction. Both inserts must use THAT transaction.
        // Never commit a transaction managed by a parent UoW.
        var owned = _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync()
            : null;
        try
        {
            var building = new Building(
                Guid.NewGuid(), name, address, description);
            _db.Set<Building>().Add(building);
            await _db.SaveChangesAsync();

            var firstGrant = BuildingOperatorAccessGrant.Issue(
                Guid.NewGuid(), trustedOwnerUserId, building.Id,
                trustedOwnerUserId, DateTime.UtcNow, null);
            _db.Set<BuildingOperatorAccessGrant>().Add(firstGrant);
            await _db.SaveChangesAsync();

            if (owned is not null)
                await owned.CommitAsync();
            return building;
        }
        catch
        {
            if (owned is not null)
                await owned.RollbackAsync();
            throw;
        }
        finally
        {
            if (owned is not null)
                await owned.DisposeAsync();
        }
    }
}
