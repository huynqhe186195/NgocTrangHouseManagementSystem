using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
using BuildingManagement.Invoices;
using BuildingManagement.Rooms;
using BuildingManagement.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Uow;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Deterministic overlapping PostgreSQL transactions using only newly
/// created, per-test business records on ngoctrang_integration_test.
/// Snapshot reader anchors an actual ABP REPEATABLE READ UOW before the
/// independent writer commits electricity and water source changes.
/// No invoices are created or issued; test fixture is hard-deleted in finally.
/// </summary>
[Collection("Phase2G2A_UtilityIntegrity")]
public sealed class InvoiceDraftConcurrentSnapshotPostgreSql_Tests
    : BuildingManagementApplicationTestBase<BuildingManagementPostgreSqlTestModule>
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IRepository<Building, Guid> _buildings;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IRepository<Contract, Guid> _contracts;
    private readonly IRepository<UtilityRate, Guid> _rates;
    private readonly IRepository<UtilityMeter, Guid> _meters;
    private readonly IRepository<MeterReading, Guid> _readings;

    private sealed record Fixture(
        Guid Building, Guid Floor, Guid Room, Guid Contract,
        Guid ElectricityRate, Guid WaterRate,
        Guid ElectricityMeter, Guid WaterMeter,
        Guid ElectricityReading, Guid WaterReading);

    public InvoiceDraftConcurrentSnapshotPostgreSql_Tests()
    {
        _scopes = GetRequiredService<IServiceScopeFactory>();
        _buildings = GetRequiredService<IRepository<Building, Guid>>();
        _floors = GetRequiredService<IRepository<Floor, Guid>>();
        _rooms = GetRequiredService<IRepository<Room, Guid>>();
        _contracts = GetRequiredService<IRepository<Contract, Guid>>();
        _rates = GetRequiredService<IRepository<UtilityRate, Guid>>();
        _meters = GetRequiredService<IRepository<UtilityMeter, Guid>>();
        _readings = GetRequiredService<IRepository<MeterReading, Guid>>();
    }

    [Fact]
    public async Task PGINV04_Meter_And_Tariff_Writes_Cannot_Mix_Versions_In_Invoice_Candidate()
    {
        var f = new Fixture(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid());

        try
        {
            await SeedAsync(f);

            // First SQL statement fixes this independent PostgreSQL
            // REPEATABLE READ snapshot before any utility data is updated.
            using (var scope = _scopes.CreateScope())
            {
                var provider = scope.ServiceProvider;
                var manager = provider.GetRequiredService<IUnitOfWorkManager>();
                using var outer = manager.Begin(
                    new AbpUnitOfWorkOptions
                    {
                        IsTransactional = true,
                        IsolationLevel = IsolationLevel.RepeatableRead
                    }, requiresNew: true);

                var anchored = await provider
                    .GetRequiredService<IRepository<Contract, Guid>>()
                    .GetAsync(f.Contract);
                Assert.Equal(f.Room, anchored.RoomId);

                // Separate Npgsql connection and COMMITTED transaction.
                // It runs while the reader's PostgreSQL transaction is open.
                await CommitNewSourceValuesAsync(f);

                var oldCandidate = await provider
                    .GetRequiredService<IInvoiceDraftCandidateAppService>()
                    .GetMonthlyCandidateAsync(f.Contract, 2026, 10);
                AssertCandidate(oldCandidate, electricityUnits: 10m,
                    electricityRate: 3500m, waterUnits: 5m, waterRate: 15000m);

                // No CompleteAsync: read-only outer UOW disposes/rolls back.
            }

            // A fresh UOW must see the newer COMMITTED *pair* of meter
            // readings and tariffs, not old values cached by first context.
            using (var fresh = _scopes.CreateScope())
            {
                var currentCandidate = await fresh.ServiceProvider
                    .GetRequiredService<IInvoiceDraftCandidateAppService>()
                    .GetMonthlyCandidateAsync(f.Contract, 2026, 10);
                AssertCandidate(currentCandidate, electricityUnits: 20m,
                    electricityRate: 7000m, waterUnits: 8m, waterRate: 30000m);
            }
        }
        finally
        {
            // Delete only exact unique IDs seeded by this test. Never drop,
            // recreate or migrate an existing test/application database.
            await CleanupAsync(f);
        }
    }

    private async Task SeedAsync(Fixture f)
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await _buildings.InsertAsync(
                new Building(f.Building, "PGINV04-" + Guid.NewGuid().ToString("N"),
                    "Test-only building"), autoSave: true);
            await _floors.InsertAsync(
                new Floor(f.Floor, f.Building, 1, "Test floor"), autoSave: true);
            await _rooms.InsertAsync(
                new Room(f.Room, f.Floor, "INV-" + Guid.NewGuid().ToString("N")[..8],
                    "Test room", 25m, 2, RoomStatus.Available), autoSave: true);

            var contract = new Contract(
                f.Contract, "PGINV04-" + Guid.NewGuid().ToString("N"),
                f.Room, new DateTime(2026, 10, 1),
                new DateTime(2026, 12, 31), 3000000m);
            contract.Activate();
            await _contracts.InsertAsync(contract, autoSave: true);

            await _rates.InsertAsync(new UtilityRate(
                f.ElectricityRate, f.Building, UtilityType.Electricity,
                3500m, new DateTime(2026, 10, 1), null), autoSave: true);
            await _rates.InsertAsync(new UtilityRate(
                f.WaterRate, f.Building, UtilityType.Water,
                15000m, new DateTime(2026, 10, 1), null), autoSave: true);

            await _meters.InsertAsync(new UtilityMeter(
                f.ElectricityMeter, f.Room, UtilityType.Electricity,
                "PGINV04E-" + Guid.NewGuid().ToString("N"),
                10m, new DateTime(2026, 10, 1)), autoSave: true);
            await _meters.InsertAsync(new UtilityMeter(
                f.WaterMeter, f.Room, UtilityType.Water,
                "PGINV04W-" + Guid.NewGuid().ToString("N"),
                100m, new DateTime(2026, 10, 1)), autoSave: true);

            await _readings.InsertAsync(new MeterReading(
                f.ElectricityReading, f.ElectricityMeter, 2026, 10,
                10m, 20m, new DateTime(2026, 10, 31)), autoSave: true);
            await _readings.InsertAsync(new MeterReading(
                f.WaterReading, f.WaterMeter, 2026, 10,
                100m, 105m, new DateTime(2026, 10, 31)), autoSave: true);
        });
    }

    private static async Task CommitNewSourceValuesAsync(Fixture f)
    {
        await using var connection = new NpgsqlConnection(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString());
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
            """
            UPDATE "BuildingManagementMeterReadings"
            SET "CurrentReading" = CASE WHEN "Id" = @electricReading
                THEN 30 ELSE 108 END
            WHERE "Id" IN (@electricReading, @waterReading);
            UPDATE "BuildingManagementUtilityRates"
            SET "UnitPrice" = CASE WHEN "Id" = @electricRate
                THEN 7000 ELSE 30000 END
            WHERE "Id" IN (@electricRate, @waterRate);
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("electricReading", f.ElectricityReading);
            command.Parameters.AddWithValue("waterReading", f.WaterReading);
            command.Parameters.AddWithValue("electricRate", f.ElectricityRate);
            command.Parameters.AddWithValue("waterRate", f.WaterRate);
            Assert.Equal(4, await command.ExecuteNonQueryAsync());
        }
        await transaction.CommitAsync();
    }

    private static void AssertCandidate(
        InvoiceDraftCandidateDto candidate, decimal electricityUnits,
        decimal electricityRate, decimal waterUnits, decimal waterRate)
    {
        Assert.Empty(candidate.Blockers);
        Assert.Equal(2, candidate.UtilityLines.Count);
        var electricity = candidate.UtilityLines.Single(
            x => x.UtilityType == UtilityType.Electricity);
        var water = candidate.UtilityLines.Single(
            x => x.UtilityType == UtilityType.Water);

        Assert.Empty(electricity.CoverageIssues);
        Assert.Empty(water.CoverageIssues);
        Assert.Equal(electricityUnits, electricity.RecordedQuantity);
        Assert.Equal(electricityRate, electricity.UnitPrice);
        Assert.Equal(electricityUnits * electricityRate,
            electricity.ProvisionalAmount);
        Assert.Equal(waterUnits, water.RecordedQuantity);
        Assert.Equal(waterRate, water.UnitPrice);
        Assert.Equal(waterUnits * waterRate, water.ProvisionalAmount);
        Assert.Equal(electricityUnits * electricityRate + waterUnits * waterRate,
            candidate.ProvisionalTotal);
    }

    private static async Task CleanupAsync(Fixture f)
    {
        await using var connection = new NpgsqlConnection(
            BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString());
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // Delete children first to honor foreign-key RESTRICT constraints.
        var deletions = new (string Table, Guid[] Ids)[]
        {
            ("BuildingManagementMeterReadings", new[] { f.ElectricityReading, f.WaterReading }),
            ("BuildingManagementUtilityMeters", new[] { f.ElectricityMeter, f.WaterMeter }),
            ("BuildingManagementUtilityRates", new[] { f.ElectricityRate, f.WaterRate }),
            ("BuildingManagementContracts", new[] { f.Contract }),
            ("BuildingManagementRooms", new[] { f.Room }),
            ("BuildingManagementFloors", new[] { f.Floor }),
            ("BuildingManagementBuildings", new[] { f.Building })
        };

        foreach (var (table, ids) in deletions)
        {
            await using var command = new NpgsqlCommand(
                $"DELETE FROM \"{table}\" WHERE \"Id\" = ANY(@ids)",
                connection, transaction);
            command.Parameters.AddWithValue("ids", ids);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }
}
