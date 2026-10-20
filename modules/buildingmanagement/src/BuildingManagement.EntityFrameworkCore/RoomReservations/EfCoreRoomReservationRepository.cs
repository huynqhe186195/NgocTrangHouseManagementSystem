using BuildingManagement.RoomReservations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using System;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace BuildingManagement.EntityFrameworkCore
    .RoomReservations;

public class EfCoreRoomReservationRepository
    : EfCoreRepository<
        BuildingManagementDbContext,
        RoomReservation,
        Guid>,
      IRoomReservationRepository
{
    public EfCoreRoomReservationRepository(
        IDbContextProvider<
            BuildingManagementDbContext>
            dbContextProvider
    ) : base(dbContextProvider)
    {
    }

    public async Task<RoomReservation?>
        FindForUpdateAsync(Guid id)
    {
        var dbContext =
            await GetDbContextAsync();

        if (dbContext.Database.IsNpgsql())
        {
            var entityType =
                dbContext.Model.FindEntityType(
                    typeof(RoomReservation)
                )
                ?? throw new InvalidOperationException(
                    "RoomReservation EF mapping was not found."
                );

            var tableName =
                entityType.GetTableName()
                ?? throw new InvalidOperationException(
                    "RoomReservation table name was not found."
                );

            var schema =
                entityType.GetSchema();

            var sqlHelper =
                dbContext.GetService<
                    ISqlGenerationHelper>();

            var table =
                sqlHelper.DelimitIdentifier(
                    tableName,
                    schema
                );

            var idColumn =
                sqlHelper.DelimitIdentifier(
                    "Id"
                );

            var sql =
                $"SELECT * FROM {table} " +
                $"WHERE {idColumn} = {{0}} " +
                "FOR UPDATE";

            return await dbContext
                .Set<RoomReservation>()
                .FromSqlRaw(
                    sql,
                    id
                )
                .SingleOrDefaultAsync();
        }

        // SQLite test fallback
        return await dbContext
            .Set<RoomReservation>()
            .SingleOrDefaultAsync(
                x => x.Id == id
            );
    }
}