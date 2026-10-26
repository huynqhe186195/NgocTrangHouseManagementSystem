using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
using BuildingManagement.Invoices;
using BuildingManagement.Rooms;
using BuildingManagement.Utilities;
using Volo.Abp.Domain.Repositories;
using Xunit;

namespace BuildingManagement.EntityFrameworkCore;

/// <summary>
/// SQLite-only, ABP repository backed scope verification. Never uses
/// app/production PostgreSQL, changes permissions, or mutates an
/// already existing business invoice. All test data is synthetic.
/// </summary>
public sealed class InvoiceDraftRevisionServerGraphIntegration_Tests
    : BuildingManagementEntityFrameworkCoreTestBase
{
    private readonly IRepository<Building, Guid> _buildings;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IRepository<Contract, Guid> _contracts;
    private readonly IRepository<Invoice, Guid> _invoices;
    private readonly IRepository<InvoiceDraftRevisionRecord, Guid> _archives;

    public InvoiceDraftRevisionServerGraphIntegration_Tests()
    {
        _buildings = GetRequiredService<IRepository<Building, Guid>>();
        _floors = GetRequiredService<IRepository<Floor, Guid>>();
        _rooms = GetRequiredService<IRepository<Room, Guid>>();
        _contracts = GetRequiredService<IRepository<Contract, Guid>>();
        _invoices = GetRequiredService<IRepository<Invoice, Guid>>();
        _archives = GetRequiredService<IRepository<InvoiceDraftRevisionRecord, Guid>>();
    }

    [Fact]
    public async Task GRAPHDB01_Real_Sqlite_Reader_Resolves_Invoice_To_Building()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var reader =
                GetRequiredService<IInvoiceDraftRevisionResourceGraphReader>();
            var graph = await reader.ReadAsync(f.InvoiceId);
            Assert.NotNull(graph);
            Assert.Equal(f.InvoiceId, graph.InvoiceId);
            Assert.Equal(f.ContractId, graph.InvoiceContractId);
            Assert.Equal(f.ContractId, graph.ContractId);
            Assert.Equal(f.RoomId, graph.InvoiceRoomId);
            Assert.Equal(f.RoomId, graph.ContractRoomId);
            Assert.Equal(f.RoomId, graph.RoomId);
            Assert.Equal(f.FloorId, graph.RoomFloorId);
            Assert.Equal(f.FloorId, graph.FloorId);
            Assert.Equal(f.BuildingId, graph.FloorBuildingId);
            Assert.Equal(f.BuildingId, graph.BuildingId);
            Assert.Equal(InvoiceStatus.Draft, graph.InvoiceStatus);
        });
    }

    [Fact]
    public async Task GRAPHDB02_Nonexistent_Invoice_Is_Not_Resolved()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var reader =
                GetRequiredService<IInvoiceDraftRevisionResourceGraphReader>();
            Assert.Null(await reader.ReadAsync(Guid.NewGuid()));
            Assert.Null(await reader.ReadAsync(Guid.Empty));
        });
    }

    [Fact]
    public async Task GRAPHDB03_Real_FK_Graph_Denies_Contract_Room_Mismatch()
    {
        var f = await SeedAsync(breakContractRoomLink: true);
        await WithUnitOfWorkAsync(async () =>
        {
            var reader =
                GetRequiredService<IInvoiceDraftRevisionResourceGraphReader>();
            var grants = new TestBuildingGrants(f.BuildingId);
            var gate = new InvoiceDraftRevisionServerAccessGate(
                new GrantedTestPrincipal(Guid.NewGuid()),
                reader, grants);
            var result = await gate.AssessDesignOnlyAsync(
                f.InvoiceId, InvoiceDraftRevisionCapability.RebuildDraft);
            Assert.Equal(InvoiceDraftRevisionAccessStatus.Denied, result);
        });
    }

    [Fact]
    public async Task GRAPHDB04_Default_DenyAll_Overrides_Simulated_Permission()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var grants = GetRequiredService<IInvoiceDraftRevisionBuildingGrants>();
            Assert.IsType<DenyAllInvoiceDraftRevisionBuildingGrants>(grants);

            var gate = new InvoiceDraftRevisionServerAccessGate(
                new GrantedTestPrincipal(Guid.NewGuid()),
                GetRequiredService<IInvoiceDraftRevisionResourceGraphReader>(),
                grants);
            Assert.Equal(InvoiceDraftRevisionAccessStatus.Denied,
                await gate.AssessDesignOnlyAsync(f.InvoiceId,
                    InvoiceDraftRevisionCapability.RebuildDraft));
            Assert.Equal(InvoiceDraftRevisionAccessStatus.Denied,
                await gate.AssessDesignOnlyAsync(f.InvoiceId,
                    InvoiceDraftRevisionCapability.ReadRevisionHistory));
        });
    }

    [Fact]
    public async Task GRAPHDB05_Deleted_Building_Cannot_Resolve_Invoice_Scope()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            // Synthetic SQLite fixture only. A FullAuditedAggregateRoot
            // uses ABP soft delete and becomes invisible to repository reads.
            await _buildings.DeleteAsync(f.BuildingId, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var reader = GetRequiredService<IInvoiceDraftRevisionResourceGraphReader>();
            Assert.Null(await reader.ReadAsync(f.InvoiceId));
        });
    }

    [Fact]
    public async Task GRAPHDB06_Access_Assessment_Does_Not_Modify_Invoice_Or_Archive()
    {
        var f = await SeedAsync();
        await WithUnitOfWorkAsync(async () =>
        {
            var gate = new InvoiceDraftRevisionServerAccessGate(
                new GrantedTestPrincipal(Guid.NewGuid()),
                GetRequiredService<IInvoiceDraftRevisionResourceGraphReader>(),
                GetRequiredService<IInvoiceDraftRevisionBuildingGrants>());

            Assert.Equal(InvoiceDraftRevisionAccessStatus.Denied,
                await gate.AssessDesignOnlyAsync(f.InvoiceId,
                    InvoiceDraftRevisionCapability.RebuildDraft));

            var saved = await _invoices.GetAsync(f.InvoiceId);
            Assert.Equal(InvoiceStatus.Draft, saved.Status);
            Assert.Equal(110000m, saved.ProvisionalUtilityTotal);
            Assert.False(await _archives.AnyAsync(x => x.InvoiceId == f.InvoiceId));
        });
    }

    private async Task<Fixture> SeedAsync(bool breakContractRoomLink = false)
    {
        var building = Guid.NewGuid();
        var floor = Guid.NewGuid();
        var room = Guid.NewGuid();
        var contractRoom = breakContractRoomLink ? Guid.NewGuid() : room;
        var contract = Guid.NewGuid();
        var invoice = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            await _buildings.InsertAsync(
                new Building(building, "Scope security test building", "SQLite only"),
                autoSave: true);
            await _floors.InsertAsync(
                new Floor(floor, building, 1, "Test floor"),
                autoSave: true);
            await _rooms.InsertAsync(
                MakeRoom(room, floor), autoSave: true);
            if (breakContractRoomLink)
                await _rooms.InsertAsync(
                    MakeRoom(contractRoom, floor), autoSave: true);
            await _contracts.InsertAsync(
                new Contract(contract,
                    "SCOPE-" + Guid.NewGuid().ToString("N"),
                    contractRoom, new DateTime(2026, 1, 1),
                    new DateTime(2027, 12, 31), 3_000_000m),
                autoSave: true);

            InvoiceDraftUtilityLineSnapshot Line(
                UtilityType type, decimal unitPrice)
            {
                var segment = new InvoiceDraftMeterSegmentSnapshot(
                    Guid.NewGuid(), Guid.NewGuid(),
                    MeterUsageSourceType.MonthlyReading,
                    new DateTime(2026, 10, 31), 0m,
                    type == UtilityType.Electricity ? 10m : 5m);
                var quantity = segment.CurrentReading - segment.PreviousReading;
                return new InvoiceDraftUtilityLineSnapshot(
                    type, Guid.NewGuid(), "SingleRateWholeMonth",
                    quantity, unitPrice, quantity * unitPrice,
                    new[] { segment }, Array.Empty<string>());
            }

            var snapshot = InvoiceDraftSnapshot.Capture(
                contract, room, 2026, 10,
                new[] { Line(UtilityType.Electricity, 3500m),
                        Line(UtilityType.Water, 15000m) });
            var draft = Invoice.CreateDraft(invoice, snapshot, Guid.NewGuid);
            await _invoices.InsertAsync(draft, autoSave: true);
        });

        return new Fixture(invoice, contract, room, floor, building);
    }

    private static Room MakeRoom(Guid id, Guid floorId)
        => new(id, floorId,
            "SEC-" + Guid.NewGuid().ToString("N")[..8],
            "Scope test room", 25m, 2, RoomStatus.Available);

    private sealed record Fixture(
        Guid InvoiceId, Guid ContractId, Guid RoomId,
        Guid FloorId, Guid BuildingId);

    private sealed class GrantedTestPrincipal :
        IInvoiceDraftRevisionPrincipalReader
    {
        private readonly Guid _id;
        public GrantedTestPrincipal(Guid id) => _id = id;
        public Task<InvoiceDraftRevisionPrincipal?> ReadAsync(
            InvoiceDraftRevisionCapability capability)
            => Task.FromResult<InvoiceDraftRevisionPrincipal?>(
                new InvoiceDraftRevisionPrincipal(_id, true));
    }

    private sealed class TestBuildingGrants : IInvoiceDraftRevisionBuildingGrants
    {
        private readonly Guid _building;
        public TestBuildingGrants(Guid building) => _building = building;
        public Task<IReadOnlyCollection<Guid>> FindGrantedBuildingIdsAsync(
            Guid operatorId)
            => Task.FromResult<IReadOnlyCollection<Guid>>(
                new[] { _building });
    }
}
