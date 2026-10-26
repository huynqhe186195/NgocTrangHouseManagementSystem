using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
using BuildingManagement.MoveOuts;
using BuildingManagement.Rooms;
using BuildingManagement.Utilities;
using Volo.Abp.Domain.Repositories;
using Xunit;

namespace BuildingManagement.EntityFrameworkCore;

/// <summary>
/// Real SQLite repositories for MoveOut/Contract/Checklist/Invoice, with a
/// deterministic fake *read-only* meter assessor. Never mutates a real DB.
/// </summary>
public sealed class MoveOutCompletionReadinessSqlite_Tests :
    BuildingManagementEntityFrameworkCoreTestBase
{
    [Fact]
    public async Task CLOSEDB01_Pending_Request_Cannot_Close_Or_Release_Room()
    {
        var seeded = await SeedAsync(approve: false);
        await WithUnitOfWorkAsync(async () =>
        {
            var service = MakeService(meterEvidence: false, seeded.ContractId, seeded.RoomId);
            var report = await service.GetAsync(seeded.RequestId);
            Assert.Contains("MoveOutNotApproved", report.Blockers);
            Assert.Contains("FinalInvoiceIssuanceNotImplemented", report.Blockers);
            Assert.False(report.CanCompleteMoveOut);
            Assert.Equal(0, report.ChecklistTotal);

            var contract = await GetRequiredService<IRepository<Contract, Guid>>()
                .GetAsync(seeded.ContractId);
            var room = await GetRequiredService<IRepository<Room, Guid>>()
                .GetAsync(seeded.RoomId);
            Assert.Equal(ContractStatus.Active, contract.Status);
            Assert.Equal(RoomStatus.Occupied, room.Status);
        });
    }

    [Fact]
    public async Task CLOSEDB02_Approved_Checklist_And_Meter_Evidence_Still_Block_Money_Close()
    {
        var seeded = await SeedAsync(approve: true);
        await WithUnitOfWorkAsync(async () =>
        {
            var service = MakeService(meterEvidence: true, seeded.ContractId, seeded.RoomId);
            var report = await service.GetAsync(seeded.RequestId);
            Assert.DoesNotContain("MoveOutNotApproved", report.Blockers);
            Assert.True(report.HasFinalMeterEvidence);
            Assert.Equal(5, report.ChecklistTotal);
            Assert.Equal(0, report.ChecklistCompleted);
            Assert.Contains("ChecklistNotCompleted:FinalInvoice", report.Blockers);
            Assert.Contains("FinalDebtClearanceNotVerified", report.Blockers);
            Assert.Contains("ContractDepositSettlementNotVerified", report.Blockers);
            Assert.False(report.CanCompleteMoveOut);

            var request = await GetRequiredService<
                IRepository<MoveOutRequest, Guid>>().GetAsync(seeded.RequestId);
            Assert.Equal(MoveOutRequestStatus.Approved, request.Status);
            var contract = await GetRequiredService<
                IRepository<Contract, Guid>>().GetAsync(seeded.ContractId);
            var room = await GetRequiredService<
                IRepository<Room, Guid>>().GetAsync(seeded.RoomId);
            Assert.Equal(ContractStatus.Active, contract.Status);
            Assert.Equal(RoomStatus.Occupied, room.Status);
        });
    }

    [Fact]
    public async Task CLOSEDB03_Unknown_Request_Returns_NotFound()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var service = MakeService(meterEvidence: false, Guid.NewGuid(), Guid.NewGuid());
            var ex = await Assert.ThrowsAsync<Volo.Abp.BusinessException>(
                () => service.GetAsync(Guid.NewGuid()));
            Assert.Equal(MoveOutErrorCodes.NotFound, ex.Code);
        });
    }

    private MoveOutCompletionReadinessAppService MakeService(
        bool meterEvidence, Guid contractId, Guid roomId)
        => new(
            GetRequiredService<IRepository<MoveOutRequest, Guid>>(),
            GetRequiredService<IRepository<MoveOutChecklistTask, Guid>>(),
            GetRequiredService<IRepository<Contract, Guid>>(),
            GetRequiredService<IRepository<BuildingManagement.Invoices.Invoice, Guid>>(),
            new FakeMeterEvidenceReader(meterEvidence, contractId, roomId));

    private async Task<Seed> SeedAsync(bool approve)
    {
        var buildingId = Guid.NewGuid();
        var floorId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            await GetRequiredService<IRepository<Building, Guid>>()
                .InsertAsync(new Building(buildingId, "Test", "SQLite"),
                    autoSave: true);
            await GetRequiredService<IRepository<Floor, Guid>>()
                .InsertAsync(new Floor(floorId, buildingId, 1, "First"),
                    autoSave: true);

            var room = new Room(roomId, floorId, "101", "Test room", 20m, 2,
                RoomStatus.Available);
            room.ChangeStatus(RoomStatus.Occupied);
            await GetRequiredService<IRepository<Room, Guid>>()
                .InsertAsync(room, autoSave: true);

            var contract = new Contract(contractId, "CLOSE-" + Guid.NewGuid()
                .ToString("N"), roomId, new DateTime(2026, 1, 1),
                null, 2000000m);
            contract.Activate();
            await GetRequiredService<IRepository<Contract, Guid>>()
                .InsertAsync(contract, autoSave: true);

            var request = new MoveOutRequest(requestId, contractId, roomId,
                new DateOnly(2026, 12, 31), "Readiness test");
            if (approve)
                request.Approve(DateTime.UtcNow, "Approved");
            await GetRequiredService<IRepository<MoveOutRequest, Guid>>()
                .InsertAsync(request, autoSave: true);

            if (approve)
            {
                foreach (var type in Enum.GetValues<MoveOutChecklistTaskType>())
                    await GetRequiredService<
                        IRepository<MoveOutChecklistTask, Guid>>()
                        .InsertAsync(new MoveOutChecklistTask(
                            Guid.NewGuid(), requestId, type), autoSave: true);
            }
        });
        return new Seed(requestId, contractId, roomId);
    }

    private sealed record Seed(Guid RequestId, Guid ContractId, Guid RoomId);

    private sealed class FakeMeterEvidenceReader :
        IMoveOutFinalMeterReadinessAppService
    {
        private readonly bool _ready;
        private readonly Guid _contractId;
        private readonly Guid _roomId;
        public FakeMeterEvidenceReader(bool ready, Guid contractId, Guid roomId)
        {
            _ready = ready;
            _contractId = contractId;
            _roomId = roomId;
        }
        public Task<MoveOutFinalMeterReadinessDto> GetAsync(Guid requestId)
            => Task.FromResult(new MoveOutFinalMeterReadinessDto
            {
                MoveOutRequestId = requestId,
                ContractId = _contractId,
                RoomId = _roomId,
                // The read service must not be fooled by a mismatched
                // fake contract/room, even when two meter lines say "ready".
                RequestStatus = MoveOutRequestStatus.Approved,
                Utilities = _ready ? new()
                {
                    new() { UtilityType = UtilityType.Electricity,
                        Status = "ReadingEvidencePresent" },
                    new() { UtilityType = UtilityType.Water,
                        Status = "ReadingEvidencePresent" }
                } : new()
            });
    }
}
