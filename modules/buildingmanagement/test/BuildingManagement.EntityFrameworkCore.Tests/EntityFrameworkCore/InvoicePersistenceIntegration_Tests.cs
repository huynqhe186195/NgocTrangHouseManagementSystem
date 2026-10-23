using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
using BuildingManagement.Invoices;
using BuildingManagement.Rooms;
using BuildingManagement.Utilities;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Domain.Repositories;
using Xunit;

namespace BuildingManagement.EntityFrameworkCore;

/// <summary>
/// Exercises ACTUAL SQLite inserts, re-hydration of child snapshots and
/// unique DB constraints. Only the isolated ABP test database is used.
/// </summary>
public sealed class InvoicePersistenceIntegration_Tests
    : BuildingManagementEntityFrameworkCoreTestBase
{
    private readonly IRepository<Building, Guid> _buildings;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IRepository<Contract, Guid> _contracts;
    private readonly IRepository<UtilityRate, Guid> _rates;
    private readonly IRepository<UtilityMeter, Guid> _meters;
    private readonly IRepository<MeterReading, Guid> _readings;
    private readonly IRepository<Invoice, Guid> _invoices;

    public InvoicePersistenceIntegration_Tests()
    {
        _buildings = GetRequiredService<IRepository<Building, Guid>>();
        _floors = GetRequiredService<IRepository<Floor, Guid>>();
        _rooms = GetRequiredService<IRepository<Room, Guid>>();
        _contracts = GetRequiredService<IRepository<Contract, Guid>>();
        _rates = GetRequiredService<IRepository<UtilityRate, Guid>>();
        _meters = GetRequiredService<IRepository<UtilityMeter, Guid>>();
        _readings = GetRequiredService<IRepository<MeterReading, Guid>>();
        _invoices = GetRequiredService<IRepository<Invoice, Guid>>();
    }

    private sealed record Fixture(
        Guid RoomId, Guid ContractId, Guid ElectricityRateId,
        Guid WaterRateId, Guid ElectricityMeterId, Guid WaterMeterId);

    private async Task<Fixture> SeedAsync()
    {
        var buildingId = Guid.NewGuid();
        var floorId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var electricityRateId = Guid.NewGuid();
        var waterRateId = Guid.NewGuid();
        var electricityMeterId = Guid.NewGuid();
        var waterMeterId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            await _buildings.InsertAsync(new Building(
                buildingId, "Draft persistence building " + Guid.NewGuid().ToString("N"),
                "Test address"), autoSave: true);
            await _floors.InsertAsync(new Floor(
                floorId, buildingId, 1, "Floor 1"), autoSave: true);
            await _rooms.InsertAsync(new Room(
                roomId, floorId, "INV-" + Guid.NewGuid().ToString("N")[..8],
                "Test room", 25m, 2, RoomStatus.Available), autoSave: true);
            await _contracts.InsertAsync(new Contract(
                contractId, "INV-CONTRACT-" + Guid.NewGuid().ToString("N"),
                roomId, new DateTime(2026, 1, 1),
                new DateTime(2027, 12, 31), 3_000_000m), autoSave: true);
            await _rates.InsertAsync(new UtilityRate(
                electricityRateId, buildingId, UtilityType.Electricity,
                3500m, new DateTime(2026, 10, 1), null), autoSave: true);
            await _rates.InsertAsync(new UtilityRate(
                waterRateId, buildingId, UtilityType.Water,
                15000m, new DateTime(2026, 10, 1), null), autoSave: true);
            await _meters.InsertAsync(new UtilityMeter(
                electricityMeterId, roomId, UtilityType.Electricity,
                "INVE-" + Guid.NewGuid().ToString("N"),
                10m, new DateTime(2026, 1, 1)), autoSave: true);
            await _meters.InsertAsync(new UtilityMeter(
                waterMeterId, roomId, UtilityType.Water,
                "INVW-" + Guid.NewGuid().ToString("N"),
                100m, new DateTime(2026, 1, 1)), autoSave: true);
        });

        return new Fixture(roomId, contractId, electricityRateId,
            waterRateId, electricityMeterId, waterMeterId);
    }

    private static Invoice CreateDraft(Fixture f,
        Guid electricityReadingId, Guid waterReadingId,
        Guid? contractId = null)
    {
        InvoiceDraftUtilityLineSnapshot MakeLine(
            UtilityType type, Guid rateId, Guid meterId,
            Guid sourceId, decimal previous, decimal current, decimal unitPrice)
        {
            var source = new InvoiceDraftMeterSegmentSnapshot(
                sourceId, meterId, MeterUsageSourceType.MonthlyReading,
                new DateTime(2026, 10, 31), previous, current);
            return new InvoiceDraftUtilityLineSnapshot(
                type, rateId, "SingleRateWholeMonth",
                current - previous, unitPrice,
                (current - previous) * unitPrice,
                new[] { source }, Array.Empty<string>());
        }

        var snapshot = InvoiceDraftSnapshot.Capture(
            contractId ?? f.ContractId, f.RoomId, 2026, 10,
            new[]
            {
                MakeLine(UtilityType.Electricity, f.ElectricityRateId,
                    f.ElectricityMeterId, electricityReadingId, 10m, 20m, 3500m),
                MakeLine(UtilityType.Water, f.WaterRateId,
                    f.WaterMeterId, waterReadingId, 100m, 105m, 15000m)
            });
        return Invoice.CreateDraft(Guid.NewGuid(), snapshot, Guid.NewGuid);
    }

    [Fact]
    public async Task INVDB01_Persists_And_Reloads_Header_Lines_And_Counter_Snapshots()
    {
        var fixture = await SeedAsync();
        var electricSource = Guid.NewGuid();
        var waterSource = Guid.NewGuid();
        var draft = CreateDraft(fixture, electricSource, waterSource);

        await WithUnitOfWorkAsync(async () =>
        {
            await _invoices.InsertAsync(draft, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var query = await _invoices.GetQueryableAsync();
            var loaded = await query.Include(x => x.Items)
                .ThenInclude(x => x.MeterSources)
                .SingleAsync(x => x.Id == draft.Id);

            Assert.Equal(InvoiceStatus.Draft, loaded.Status);
            Assert.Equal(fixture.ContractId, loaded.ContractId);
            Assert.Equal(2026, loaded.BillingYear);
            Assert.Equal(10, loaded.BillingMonth);
            Assert.Equal(110000m, loaded.ProvisionalUtilityTotal);
            Assert.Equal(2, loaded.Items.Count);

            var electric = loaded.Items.Single(x =>
                x.UtilityType == UtilityType.Electricity);
            Assert.Equal(10m, electric.RecordedQuantity);
            Assert.Equal(3500m, electric.UnitPrice);
            Assert.Equal(35000m, electric.ProvisionalAmount);
            Assert.Equal(electricSource, Assert.Single(electric.MeterSources).SourceId);

            var water = loaded.Items.Single(x =>
                x.UtilityType == UtilityType.Water);
            Assert.Equal(75000m, water.ProvisionalAmount);
            Assert.Equal(waterSource, Assert.Single(water.MeterSources).SourceId);
        });
    }

    [Fact]
    public async Task INVDB02_Database_Rejects_Second_Invoice_For_Same_Contract_Month()
    {
        var fixture = await SeedAsync();
        var first = CreateDraft(fixture, Guid.NewGuid(), Guid.NewGuid());
        await WithUnitOfWorkAsync(async () =>
        {
            await _invoices.InsertAsync(first, autoSave: true);
        });

        var second = CreateDraft(fixture, Guid.NewGuid(), Guid.NewGuid());
        var failure = await Record.ExceptionAsync(async () =>
        {
            await WithUnitOfWorkAsync(async () =>
            {
                await _invoices.InsertAsync(second, autoSave: true);
            });
        });

        Assert.NotNull(failure);
        Assert.Contains("UNIQUE", failure.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task INVDB03_Database_Rejects_Reused_Reading_Across_Contracts()
    {
        var fixture = await SeedAsync();
        var electricSource = Guid.NewGuid();
        var first = CreateDraft(fixture, electricSource, Guid.NewGuid());
        await WithUnitOfWorkAsync(async () =>
        {
            await _invoices.InsertAsync(first, autoSave: true);
        });

        // A different room + contract ensures this test exercises the
        // global SOURCE uniqueness, not the new room-month constraint.
        var separateRoom = await SeedAsync();
        var second = CreateDraft(
            separateRoom, electricSource, Guid.NewGuid());
        var failure = await Record.ExceptionAsync(async () =>
        {
            await WithUnitOfWorkAsync(async () =>
            {
                await _invoices.InsertAsync(second, autoSave: true);
            });
        });

        Assert.NotNull(failure);
        Assert.Contains("UNIQUE", failure.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task INVDB04_Changing_Rate_Does_Not_Reprice_Stored_Draft_Snapshot()
    {
        var fixture = await SeedAsync();
        var draft = CreateDraft(fixture, Guid.NewGuid(), Guid.NewGuid());
        await WithUnitOfWorkAsync(async () =>
        {
            await _invoices.InsertAsync(draft, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var rate = await _rates.GetAsync(fixture.ElectricityRateId);
            rate.Update(
                rate.BuildingId, rate.UtilityType, 9999m,
                rate.EffectiveFrom, rate.EffectiveTo, rate.Notes);
            await _rates.UpdateAsync(rate, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var query = await _invoices.GetQueryableAsync();
            var stored = await query.Include(x => x.Items)
                .ThenInclude(x => x.MeterSources)
                .SingleAsync(x => x.Id == draft.Id);
            var electricity = stored.Items.Single(x =>
                x.UtilityType == UtilityType.Electricity);

            Assert.Equal(3500m, electricity.UnitPrice);
            Assert.Equal(35000m, electricity.ProvisionalAmount);
            Assert.Equal(110000m, stored.ProvisionalUtilityTotal);
            Assert.Equal(10m,
                Assert.Single(electricity.MeterSources).Quantity);
        });
    }


    [Fact]
    public async Task INVDB05_Rejects_Two_Contracts_For_One_Room_Month()
    {
        var fixture = await SeedAsync();
        var first = CreateDraft(fixture, Guid.NewGuid(), Guid.NewGuid());
        await WithUnitOfWorkAsync(async () =>
        {
            await _invoices.InsertAsync(first, autoSave: true);
        });

        // Two different contract numbers are still the SAME physical room.
        // Different meter source IDs bypass the global-source guard, so
        // the room-month unique index must reject the second draft.
        var otherContractId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await _contracts.InsertAsync(new Contract(
                otherContractId, "INV-ROOM-CONFLICT-" + Guid.NewGuid().ToString("N"),
                fixture.RoomId, new DateTime(2026, 1, 1),
                new DateTime(2027, 12, 31), 3_000_000m), autoSave: true);
        });

        var second = CreateDraft(
            fixture, Guid.NewGuid(), Guid.NewGuid(), otherContractId);
        var error = await Record.ExceptionAsync(async () =>
        {
            await WithUnitOfWorkAsync(async () =>
            {
                await _invoices.InsertAsync(second, autoSave: true);
            });
        });

        Assert.NotNull(error);
        Assert.Contains("UNIQUE", error.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BuildingManagementInvoices", error.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public async Task INVDB06_Source_Reading_SoftDelete_Does_Not_Erase_Draft_Evidence()
    {
        var fixture = await SeedAsync();
        var physicalReadId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await _readings.InsertAsync(new MeterReading(
                physicalReadId, fixture.ElectricityMeterId,
                2026, 10, 10m, 20m,
                new DateTime(2026, 10, 31)), autoSave: true);
        });

        var invoice = CreateDraft(fixture, physicalReadId, Guid.NewGuid());
        await WithUnitOfWorkAsync(async () =>
        {
            await _invoices.InsertAsync(invoice, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            await _readings.DeleteAsync(physicalReadId, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var query = await _invoices.GetQueryableAsync();
            var stored = await query.Include(x => x.Items)
                .ThenInclude(x => x.MeterSources)
                .SingleAsync(x => x.Id == invoice.Id);
            var electric = stored.Items.Single(x =>
                x.UtilityType == UtilityType.Electricity);
            var evidence = Assert.Single(electric.MeterSources);

            Assert.Equal(physicalReadId, evidence.SourceId);
            Assert.Equal(10m, evidence.PreviousReading);
            Assert.Equal(20m, evidence.CurrentReading);
            Assert.Equal(10m, evidence.Quantity);
            Assert.Equal(3500m, electric.UnitPrice);
            Assert.Equal(35000m, electric.ProvisionalAmount);
        });
    }

}
