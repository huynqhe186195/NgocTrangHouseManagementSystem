using System;
using System.Threading.Tasks;
using BuildingManagement.Utilities;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;

namespace BuildingManagement.EntityFrameworkCore.Utilities;

/// <summary>
/// PostgreSQL row locks serialize closing a meter with posting/updating its
/// regular readings. SQLite EF tests run sequentially; PostgreSQL is the
/// production concurrency authority.
/// </summary>
public sealed class EfCoreUtilityMeterLock : IUtilityMeterLock, ITransientDependency
{
    private readonly IDbContextProvider<BuildingManagementDbContext> _contexts;

    public EfCoreUtilityMeterLock(IDbContextProvider<BuildingManagementDbContext> contexts)
    {
        _contexts = contexts;
    }

    public async Task AcquireAsync(Guid utilityMeterId)
    {
        if (utilityMeterId == Guid.Empty) return;
        var db = await _contexts.GetDbContextAsync();
        if (!db.Database.IsNpgsql()) return;
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "Physical meter mutations require a transactional unit of work.");

        // Raw SELECT executed through the active EF transaction; row-level lock
        // survives until the enclosing ABP UOW commits/rolls back.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"SELECT ""Id"" FROM ""BuildingManagementUtilityMeters""
                WHERE ""Id"" = {utilityMeterId} FOR UPDATE");
    }
}
