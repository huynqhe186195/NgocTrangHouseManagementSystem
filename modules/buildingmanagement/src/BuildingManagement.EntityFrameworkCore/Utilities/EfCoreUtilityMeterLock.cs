using System;
using System.Threading.Tasks;
using BuildingManagement.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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

        // SELECT FOR UPDATE returns a scalar. ExecuteSql* is intended for
        // non-query statements; use the same EF connection and transaction
        // with ExecuteScalarAsync to reliably acquire the PostgreSQL row lock.
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction.GetDbTransaction();
        command.CommandText = """
            SELECT "Id"
            FROM "BuildingManagementUtilityMeters"
            WHERE "Id" = @meterId
            FOR UPDATE
            """;

        var meterIdParameter = command.CreateParameter();
        meterIdParameter.ParameterName = "meterId";
        meterIdParameter.Value = utilityMeterId;
        command.Parameters.Add(meterIdParameter);

        // The row lock stays held until the enclosing ABP UOW commits or rolls back.
        await command.ExecuteScalarAsync();
    }
}
