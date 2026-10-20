using BuildingManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace BuildingManagement.EntityFrameworkCore.Contracts;

public class EfCoreContractRepository
    : EfCoreRepository<
        BuildingManagementDbContext,
        Contract,
        Guid>,
      IContractRepository
{
    public EfCoreContractRepository(
        IDbContextProvider<
            BuildingManagementDbContext>
            dbContextProvider
    ) : base(dbContextProvider)
    {
    }

    public async Task<Contract?>
        FindForUpdateAsync(Guid id)
    {
        var dbContext =
            await GetDbContextAsync();

        /*
         * Production PostgreSQL:
         * acquire a row-level lock.
         *
         * SQLite test provider does not support
         * SELECT ... FOR UPDATE, so normal tracking
         * query is used in tests.
         */
        if (dbContext.Database.IsNpgsql())
        {
            return await dbContext
                .Contracts
                .FromSqlInterpolated(
                    $"""
                    SELECT *
                    FROM "BuildingManagementContracts"
                    WHERE "Id" = {id}
                    FOR UPDATE
                    """
                )
                .SingleOrDefaultAsync();
        }

        return await dbContext
            .Contracts
            .SingleOrDefaultAsync(
                x => x.Id == id
            );
    }
}