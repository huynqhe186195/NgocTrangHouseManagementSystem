using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Floors;
using BuildingManagement.Rooms;
using BuildingManagement.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Domain.Repositories;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

// These tests run against a shared database. Keep this xUnit collection
// isolated from other PostgreSQL test classes, while still running two
// competing requests concurrently inside PGUTIL01 and PGUTIL02.
[CollectionDefinition("Phase2G2A_UtilityIntegrity", DisableParallelization = true)]
public sealed class Phase2G2A_UtilityIntegrityCollection
{
}

// Must run against ngoctrang_integration_test after applying the dedicated
// Phase2G2A_TestSchema.sql. Existing PG tests use EnsureCreated(), not Migrate().
[Collection("Phase2G2A_UtilityIntegrity")]
public sealed class UtilityIntegrityPostgreSql_Tests
    : BuildingManagementApplicationTestBase<BuildingManagementPostgreSqlTestModule>
{
    private readonly IRepository<Building, Guid> _buildings;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IRepository<UtilityMeter, Guid> _meters;
    private readonly IRepository<UtilityRate, Guid> _rates;
    private readonly IServiceScopeFactory _scopeFactory;

    public UtilityIntegrityPostgreSql_Tests()
    {
        _buildings = GetRequiredService<IRepository<Building, Guid>>();
        _floors = GetRequiredService<IRepository<Floor, Guid>>();
        _rooms = GetRequiredService<IRepository<Room, Guid>>();
        _meters = GetRequiredService<IRepository<UtilityMeter, Guid>>();
        _rates = GetRequiredService<IRepository<UtilityRate, Guid>>();
        _scopeFactory = GetRequiredService<IServiceScopeFactory>();
    }

    [Fact]
    public async Task PGUTIL00_Database_Enforces_ActiveMeter_And_RateOverlap()
    {
        await using var connection = new NpgsqlConnection(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString());
        await connection.OpenAsync();

        await using var meterIndexQuery = new NpgsqlCommand(
            @"SELECT COUNT(*)
                FROM pg_indexes
                WHERE schemaname = 'public'
                  AND tablename = 'BuildingManagementUtilityMeters'
                  AND indexname = 'IX_BuildingManagementUtilityMeters_RoomId_UtilityType'
                  AND indexdef ILIKE '%UNIQUE%'
                  AND indexdef ILIKE '%IsActive%'
                  AND indexdef ILIKE '%IsDeleted%'", connection);

        Convert.ToInt32(await meterIndexQuery.ExecuteScalarAsync()).ShouldBe(1);

        await using var exclusionQuery = new NpgsqlCommand(
            @"SELECT COUNT(*)
                FROM pg_constraint c
                JOIN pg_class t ON t.oid = c.conrelid
                WHERE t.relname = 'BuildingManagementUtilityRates'
                  AND c.conname = 'EX_BuildingManagementUtilityRates_NoOverlap'
                  AND c.contype = 'x'", connection);

        Convert.ToInt32(await exclusionQuery.ExecuteScalarAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task PGUTIL01_Concurrent_ActiveMeters_Only_One_Succeeds()
    {
        var roomId = await CreateRoomAsync();

        Task<Exception?> first;
        Task<Exception?> second;
        // Task.Run normally flows the caller's AsyncLocal/ABP ambient UOW.
        // Each competing request must start with an independent DI scope/UOW.
        using (ExecutionContext.SuppressFlow())
        {
            first = Task.Run(() => TryCreateMeterAsync(roomId));
            second = Task.Run(() => TryCreateMeterAsync(roomId));
        }

        var results = await Task.WhenAll(first, second)
            .WaitAsync(TimeSpan.FromSeconds(35));

        results.Count(x => x is null).ShouldBe(1);
        results.Count(x => x is not null).ShouldBe(1);

        var error = results.Single(x => x is not null)!;
        if (error is BusinessException businessError)
        {
            businessError.Code.ShouldBe(
                BuildingManagementErrorCodes.ActiveUtilityMeterAlreadyExists);
        }
        else
        {
            HasPostgresError(error, PostgresErrorCodes.UniqueViolation)
                .ShouldBeTrue(
                    "Expected PostgreSQL unique violation (23505). " +
                    "Actual exception: " + error);
        }

        var meters = await _meters.GetListAsync();
        meters.Count(x => x.RoomId == roomId &&
                          x.UtilityType == UtilityType.Electricity &&
                          x.IsActive).ShouldBe(1);
    }

    [Fact]
    public async Task PGUTIL02_Concurrent_OverlappingRates_Only_One_Succeeds()
    {
        var buildingId = await CreateBuildingAsync();

        Task<Exception?> first;
        Task<Exception?> second;
        using (ExecutionContext.SuppressFlow())
        {
            first = Task.Run(() => TryCreateRateAsync(
                buildingId, new DateTime(2027, 1, 1), new DateTime(2027, 6, 30)));
            second = Task.Run(() => TryCreateRateAsync(
                buildingId, new DateTime(2027, 3, 1), new DateTime(2027, 9, 30)));
        }

        var results = await Task.WhenAll(first, second)
            .WaitAsync(TimeSpan.FromSeconds(35));

        results.Count(x => x is null).ShouldBe(1);
        results.Count(x => x is not null).ShouldBe(1);

        var error = results.Single(x => x is not null)!;
        if (error is BusinessException businessError)
        {
            businessError.Code.ShouldBe(
                BuildingManagementErrorCodes.UtilityRatePeriodOverlaps);
        }
        else
        {
            HasPostgresError(error, PostgresErrorCodes.ExclusionViolation)
                .ShouldBeTrue(
                    "Expected PostgreSQL exclusion violation (23P01). " +
                    "Actual exception: " + error);
        }

        var rates = await _rates.GetListAsync();
        rates.Count(x => x.BuildingId == buildingId &&
                         x.UtilityType == UtilityType.Electricity).ShouldBe(1);
    }

    private async Task<Exception?> TryCreateMeterAsync(Guid roomId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider
                .GetRequiredService<IUtilityMeterAppService>();
            await service.CreateAsync(new CreateUtilityMeterDto
            {
                RoomId = roomId,
                UtilityType = UtilityType.Electricity,
                MeterCode = "PGUTIL-" + Guid.NewGuid().ToString("N"),
                InitialReading = 0m,
                InstalledDate = new DateTime(2026, 1, 1)
            });
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private async Task<Exception?> TryCreateRateAsync(
        Guid buildingId, DateTime from, DateTime to)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider
                .GetRequiredService<IUtilityRateAppService>();
            await service.CreateAsync(new CreateUtilityRateDto
            {
                BuildingId = buildingId,
                UtilityType = UtilityType.Electricity,
                UnitPrice = 4_000m,
                EffectiveFrom = from,
                EffectiveTo = to
            });
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private async Task<Guid> CreateBuildingAsync()
    {
        var id = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await _buildings.InsertAsync(
                new Building(id, "PGUTIL-" + Guid.NewGuid().ToString("N"), "Address"),
                autoSave: true);
        });
        return id;
    }

    private async Task<Guid> CreateRoomAsync()
    {
        var buildingId = Guid.NewGuid();
        var floorId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await _buildings.InsertAsync(
                new Building(buildingId, "PGUTIL-" + Guid.NewGuid().ToString("N"), "Address"),
                autoSave: true);
            await _floors.InsertAsync(
                new Floor(floorId, buildingId, 1, "Floor 1"), autoSave: true);
            await _rooms.InsertAsync(
                new Room(roomId, floorId, "G2A", "PGUTIL Room", 25, 2,
                    RoomStatus.Available),
                autoSave: true);
        });
        return roomId;
    }

    private static bool HasPostgresError(Exception exception, string code)
    {
        for (Exception? current = exception; current is not null;
             current = current.InnerException)
        {
            if (current is PostgresException postgres && postgres.SqlState == code)
            {
                return true;
            }
        }
        return false;
    }
}
