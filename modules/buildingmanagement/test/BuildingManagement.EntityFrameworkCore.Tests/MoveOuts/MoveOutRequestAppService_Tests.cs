using System;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
using BuildingManagement.MoveOuts;
using BuildingManagement.Rooms;
using BuildingManagement.Tenants;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Timing;
using Xunit;

namespace BuildingManagement.EntityFrameworkCore;

public sealed class MoveOutRequestAppService_Tests
    : BuildingManagementApplicationTestBase<BuildingManagementEntityFrameworkCoreTestModule>
{
    private readonly IMoveOutRequestAppService _moveOut;
    private readonly IContractAppService _contracts;
    private readonly ITenantAppService _tenants;
    private readonly IRepository<Building, Guid> _buildings;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IClock _clock;

    public MoveOutRequestAppService_Tests()
    {
        _moveOut = GetRequiredService<IMoveOutRequestAppService>();
        _contracts = GetRequiredService<IContractAppService>();
        _tenants = GetRequiredService<ITenantAppService>();
        _buildings = GetRequiredService<IRepository<Building, Guid>>();
        _floors = GetRequiredService<IRepository<Floor, Guid>>();
        _rooms = GetRequiredService<IRepository<Room, Guid>>();
        _clock = GetRequiredService<IClock>();
    }

    private DateOnly Earliest => MoveOutNoticePolicy.EarliestPermittedMoveOutDate(
        MoveOutNoticePolicy.VietnamDate(_clock.Now));

    private async Task<Guid> CreateRoomAsync()
    {
        var buildingId = Guid.NewGuid();
        var floorId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await _buildings.InsertAsync(
                new Building(buildingId, $"MOUT Building {Guid.NewGuid():N}", "Address"),
                autoSave: true);
            await _floors.InsertAsync(new Floor(floorId, buildingId, 1, "Floor 1"), autoSave: true);
            await _rooms.InsertAsync(
                new Room(roomId, floorId, "M101", "MOUT Room", 25, 2, RoomStatus.Available),
                autoSave: true);
        });
        return roomId;
    }

    private async Task<ContractDto> CreateContractAsync(bool activate)
    {
        var roomId = await CreateRoomAsync();
        var contract = await _contracts.CreateAsync(new CreateContractDto
        {
            ContractNumber = $"MOUT-HD-{Guid.NewGuid():N}",
            RoomId = roomId,
            StartDate = _clock.Now.Date.AddDays(-10),
            EndDate = _clock.Now.Date.AddMonths(6),
            MonthlyRent = 3_000_000m
        });
        if (!activate) return contract;

        var tenantNumber = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0);
        var tenant = await _tenants.CreateAsync(new CreateTenantDto
        {
            FullName = $"MOUT tenant {Guid.NewGuid():N}",
            PhoneNumber = $"09{tenantNumber % 100_000_000UL:D8}",
            IdentityType = IdentityType.CitizenId,
            IdentityNumber = $"079{tenantNumber % 1_000_000_000UL:D9}"
        });
        await _contracts.AddTenantAsync(contract.Id,
            new AddContractTenantDto { TenantId = tenant.Id,
                Role = ContractTenantRole.PrimaryTenant });
        return await _contracts.ActivateAsync(contract.Id);
    }

    private Task<MoveOutRequestDto> SubmitAsync(Guid contractId, DateOnly? date = null)
        => _moveOut.CreateAsync(new CreateMoveOutRequestDto
        {
            ContractId = contractId,
            RequestedMoveOutDate = date ?? Earliest,
            Reason = "Moving out at month end"
        });

    [Fact]
    public async Task MOUT01_Should_Reject_NonActive_Contract()
    {
        var draft = await CreateContractAsync(activate: false);
        var error = await Should.ThrowAsync<BusinessException>(() => SubmitAsync(draft.Id));
        error.Code.ShouldBe(MoveOutErrorCodes.ContractNotActive);
    }

    [Fact]
    public async Task MOUT02_Should_Reject_Short_Notice_And_NonMonthEnd()
    {
        var active = await CreateContractAsync(activate: true);
        var today = MoveOutNoticePolicy.VietnamDate(_clock.Now);
        var soon = new DateOnly(today.Year, today.Month,
            DateTime.DaysInMonth(today.Year, today.Month));
        var shortError = await Should.ThrowAsync<BusinessException>(
            () => SubmitAsync(active.Id, soon));
        shortError.Code.ShouldBe(MoveOutErrorCodes.InsufficientNotice);

        var nonMonthEnd = Earliest.AddDays(-1);
        var dateError = await Should.ThrowAsync<BusinessException>(
            () => SubmitAsync(active.Id, nonMonthEnd));
        dateError.Code.ShouldBe(MoveOutErrorCodes.InvalidMoveOutDate);
    }

    [Fact]
    public async Task MOUT03_Should_Reject_Second_Open_Request()
    {
        var active = await CreateContractAsync(activate: true);
        await SubmitAsync(active.Id);
        var error = await Should.ThrowAsync<BusinessException>(() => SubmitAsync(active.Id));
        error.Code.ShouldBe(MoveOutErrorCodes.OpenRequestAlreadyExists);
    }

    [Fact]
    public async Task MOUT04_Approval_Must_Keep_Contract_Active_And_Room_Occupied()
    {
        var active = await CreateContractAsync(activate: true);
        var request = await SubmitAsync(active.Id);
        var approved = await _moveOut.ApproveAsync(
            request.Id, new ApproveMoveOutRequestDto { ReviewNotes = "Approved" });

        approved.Status.ShouldBe(MoveOutRequestStatus.Approved);
        approved.ApprovedMoveOutDate.ShouldBe(request.RequestedMoveOutDate);
        var tasks = await _moveOut.GetChecklistAsync(request.Id);
        tasks.Count.ShouldBe(5);
        tasks.ShouldAllBe(x => !x.IsCompleted);
        (await _contracts.GetAsync(active.Id)).Status.ShouldBe(ContractStatus.Active);
        RoomStatus roomStatus = RoomStatus.Available;
        await WithUnitOfWorkAsync(async () =>
        {
            roomStatus = (await _rooms.GetAsync(active.RoomId)).Status;
        });
        roomStatus.ShouldBe(RoomStatus.Occupied);
    }

    [Fact]
    public async Task MOUT05_Rejection_Must_Not_Release_Room()
    {
        var active = await CreateContractAsync(activate: true);
        var request = await SubmitAsync(active.Id);
        var rejected = await _moveOut.RejectAsync(request.Id,
            new RejectMoveOutRequestDto { Reason = "Notice not agreed" });

        rejected.Status.ShouldBe(MoveOutRequestStatus.Rejected);
        rejected.ReviewNotes.ShouldBe("Notice not agreed");
        (await _moveOut.GetChecklistAsync(request.Id)).ShouldBeEmpty();
        (await _contracts.GetAsync(active.Id)).Status.ShouldBe(ContractStatus.Active);
        RoomStatus roomStatus = RoomStatus.Available;
        await WithUnitOfWorkAsync(async () =>
        {
            roomStatus = (await _rooms.GetAsync(active.RoomId)).Status;
        });
        roomStatus.ShouldBe(RoomStatus.Occupied);
    }

    [Fact]
    public async Task MOUT06_Cancelled_Request_Can_Be_Replaced_But_Approved_Cannot()
    {
        var active = await CreateContractAsync(activate: true);
        var original = await SubmitAsync(active.Id);
        var cancelled = await _moveOut.CancelAsync(original.Id);
        cancelled.Status.ShouldBe(MoveOutRequestStatus.Cancelled);

        var replacement = await SubmitAsync(active.Id);
        (await _moveOut.ApproveAsync(replacement.Id, new ApproveMoveOutRequestDto()))
            .Status.ShouldBe(MoveOutRequestStatus.Approved);
        var error = await Should.ThrowAsync<BusinessException>(
            () => _moveOut.ApproveAsync(replacement.Id, new ApproveMoveOutRequestDto()));
        error.Code.ShouldBe(MoveOutErrorCodes.CannotApprove);
    }
}
