using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Floors;
using BuildingManagement.Rooms;
using Shouldly;
using System;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;
using Xunit;

namespace BuildingManagement.EntityFrameworkCore
    .DepositSettlements;

public class DepositSettlementRepository_Tests
    : BuildingManagementEntityFrameworkCoreTestBase
{
    private readonly IRepository<Building, Guid>
        _buildingRepository;

    private readonly IRepository<Floor, Guid>
        _floorRepository;

    private readonly IRepository<Room, Guid>
        _roomRepository;

    private readonly IRepository<Contract, Guid>
        _contractRepository;

    private readonly IRepository<DepositSettlement, Guid>
        _settlementRepository;

    public DepositSettlementRepository_Tests()
    {
        _buildingRepository =
            GetRequiredService<
                IRepository<Building, Guid>>();

        _floorRepository =
            GetRequiredService<
                IRepository<Floor, Guid>>();

        _roomRepository =
            GetRequiredService<
                IRepository<Room, Guid>>();

        _contractRepository =
            GetRequiredService<
                IRepository<Contract, Guid>>();

        _settlementRepository =
            GetRequiredService<
                IRepository<DepositSettlement, Guid>>();
    }

    [Fact]
    public async Task
        Should_Persist_And_Load_DepositSettlement()
    {
        var buildingId = Guid.NewGuid();
        var floorId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var settlementId = Guid.NewGuid();

        await WithUnitOfWorkAsync(
            async () =>
            {
                var building =
                    new Building(
                        buildingId,
                        $"Test Building {Guid.NewGuid():N}",
                        "Test Address"
                    );

                await _buildingRepository.InsertAsync(
                    building,
                    autoSave: true
                );

                var floor =
                    new Floor(
                        floorId,
                        buildingId,
                        1,
                        "Floor 1"
                    );

                await _floorRepository.InsertAsync(
                    floor,
                    autoSave: true
                );

                var room =
                    new Room(
                        roomId,
                        floorId,
                        "101",
                        "Room 101",
                        25,
                        2,
                        RoomStatus.Available
                    );

                await _roomRepository.InsertAsync(
                    room,
                    autoSave: true
                );

                var contract =
                    new Contract(
                        contractId,
                        $"HD-SETTLEMENT-{Guid.NewGuid():N}",
                        roomId,
                        new DateTime(2026, 10, 1),
                        new DateTime(2027, 9, 30),
                        3_000_000,
                        "Settlement persistence test"
                    );

                await _contractRepository.InsertAsync(
                    contract,
                    autoSave: true
                );

                var settlement =
                    new DepositSettlement(
                        settlementId,
                        DepositSettlementType.Forfeit,
                        3_000_000,
                        sourceContractId: contractId,
                        notes: "  Early termination deposit  "
                    );

                await _settlementRepository.InsertAsync(
                    settlement,
                    autoSave: true
                );
            }
        );

        var loaded =
            await WithUnitOfWorkAsync(
                async () =>
                    await _settlementRepository
                        .GetAsync(settlementId)
            );

        loaded.Id.ShouldBe(settlementId);

        loaded.Type.ShouldBe(
            DepositSettlementType.Forfeit
        );

        loaded.Status.ShouldBe(
            DepositSettlementStatus.Pending
        );

        loaded.Amount.ShouldBe(3_000_000);

        loaded.SourceContractId.ShouldBe(
            contractId
        );

        loaded.PaymentId.ShouldBeNull();

        loaded.SourceRoomReservationId
            .ShouldBeNull();

        loaded.TargetContractId.ShouldBeNull();

        loaded.CompletedAt.ShouldBeNull();

        loaded.Notes.ShouldBe(
            "Early termination deposit"
        );
    }
}