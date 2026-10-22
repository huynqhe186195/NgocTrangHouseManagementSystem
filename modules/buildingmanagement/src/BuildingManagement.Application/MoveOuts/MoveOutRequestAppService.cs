using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Timing;
using Volo.Abp.Uow;

namespace BuildingManagement.MoveOuts;

/// <summary>
/// Phase 2G.1: schedules move-out, but NEVER ends the Contract,
/// refunds security deposits or releases the Room.
/// All mutating operations acquire the Contract lock FIRST.
/// </summary>
public class MoveOutRequestAppService : ApplicationService, IMoveOutRequestAppService
{
    private readonly IRepository<MoveOutRequest, Guid> _requests;
    private readonly IRepository<MoveOutChecklistTask, Guid> _checklist;
    private readonly IContractRepository _contractLocks;
    private readonly IClock _clock;

    public MoveOutRequestAppService(
        IRepository<MoveOutRequest, Guid> requests,
        IRepository<MoveOutChecklistTask, Guid> checklist,
        IContractRepository contractLocks,
        IClock clock)
    {
        _requests = requests;
        _checklist = checklist;
        _contractLocks = contractLocks;
        _clock = clock;
    }

    [UnitOfWork(isTransactional: true)]
    public virtual async Task<MoveOutRequestDto> CreateAsync(CreateMoveOutRequestDto input)
    {
        var contract = await LockActiveContractAsync(input.ContractId);
        var submittedOn = MoveOutNoticePolicy.VietnamDate(_clock.Now);
        ValidateMoveOutDate(submittedOn, input.RequestedMoveOutDate);
        ValidateOptionalText(input.Reason);

        if (await HasOpenRequestAsync(contract.Id))
            throw new BusinessException(MoveOutErrorCodes.OpenRequestAlreadyExists);

        var request = new MoveOutRequest(
            GuidGenerator.Create(), contract.Id, contract.RoomId,
            input.RequestedMoveOutDate, input.Reason);

        await _requests.InsertAsync(request, autoSave: true);
        return ToDto(request);
    }

    public virtual async Task<MoveOutRequestDto> GetAsync(Guid id)
    {
        var request = await _requests.FindAsync(id);
        if (request is null)
            throw new BusinessException(MoveOutErrorCodes.NotFound);
        return ToDto(request);
    }

    public virtual async Task<List<MoveOutRequestDto>> GetByContractAsync(Guid contractId)
    {
        var query = await _requests.GetQueryableAsync();
        var requests = await AsyncExecuter.ToListAsync(
            query.Where(x => x.ContractId == contractId)
                .OrderByDescending(x => x.CreationTime));
        return requests.Select(ToDto).ToList();
    }

    public virtual async Task<List<MoveOutChecklistTaskDto>> GetChecklistAsync(Guid id)
    {
        await GetExistingAsync(id);
        var tasks = await _checklist.GetListAsync(x => x.MoveOutRequestId == id);
        return tasks.OrderBy(x => x.Type).Select(x => new MoveOutChecklistTaskDto
        {
            Id = x.Id,
            MoveOutRequestId = x.MoveOutRequestId,
            Type = x.Type,
            IsCompleted = x.IsCompleted,
            CompletedAt = x.CompletedAt
        }).ToList();
    }

    [UnitOfWork(isTransactional: true)]
    public virtual async Task<MoveOutRequestDto> ApproveAsync(
        Guid id, ApproveMoveOutRequestDto input)
    {
        // Look up the scalar ContractId WITHOUT tracking the MoveOutRequest.
        // Then lock Contract -> load Request -> validate -> update.
        var contractId = await GetContractIdAsync(id);
        await LockActiveContractAsync(contractId);
        var request = await GetExistingAsync(id);
        if (request.Status != MoveOutRequestStatus.Pending)
            throw new BusinessException(MoveOutErrorCodes.CannotApprove);

        // The notice period is measured from the original submission date,
        // NOT the owner's approval date.
        var submittedOn = MoveOutNoticePolicy.VietnamDate(request.CreationTime);
        ValidateMoveOutDate(submittedOn, request.RequestedMoveOutDate);
        if (request.RequestedMoveOutDate < MoveOutNoticePolicy.VietnamDate(_clock.Now))
            throw new BusinessException(MoveOutErrorCodes.InvalidMoveOutDate);

        ValidateOptionalText(input.ReviewNotes);
        request.Approve(_clock.Now, input.ReviewNotes);
        await _requests.UpdateAsync(request, autoSave: true);

        // Keep checklist creation inside the same transaction as approval.
        foreach (var taskType in Enum.GetValues<MoveOutChecklistTaskType>())
        {
            await _checklist.InsertAsync(
                new MoveOutChecklistTask(GuidGenerator.Create(), request.Id, taskType),
                autoSave: true);
        }

        return ToDto(request);
    }

    [UnitOfWork(isTransactional: true)]
    public virtual async Task<MoveOutRequestDto> RejectAsync(
        Guid id, RejectMoveOutRequestDto input)
    {
        var contractId = await GetContractIdAsync(id);
        await LockActiveContractAsync(contractId);
        var request = await GetExistingAsync(id);
        if (request.Status != MoveOutRequestStatus.Pending)
            throw new BusinessException(MoveOutErrorCodes.CannotReject);
        if (string.IsNullOrWhiteSpace(input.Reason))
            throw new BusinessException(MoveOutErrorCodes.RejectionReasonRequired);
        ValidateOptionalText(input.Reason);

        request.Reject(_clock.Now, input.Reason);
        await _requests.UpdateAsync(request, autoSave: true);
        return ToDto(request);
    }

    [UnitOfWork(isTransactional: true)]
    public virtual async Task<MoveOutRequestDto> CancelAsync(Guid id)
    {
        var contractId = await GetContractIdAsync(id);
        await LockActiveContractAsync(contractId);
        var request = await GetExistingAsync(id);
        if (request.Status != MoveOutRequestStatus.Pending)
            throw new BusinessException(MoveOutErrorCodes.CannotCancel);

        request.Cancel(_clock.Now);
        await _requests.UpdateAsync(request, autoSave: true);
        return ToDto(request);
    }

    private async Task<Contract> LockActiveContractAsync(Guid id)
    {
        var contract = id == Guid.Empty
            ? null
            : await _contractLocks.FindForUpdateAsync(id);

        if (contract is null)
            throw new BusinessException(BuildingManagementErrorCodes.ContractNotFound);
        if (contract.Status != ContractStatus.Active)
            throw new BusinessException(MoveOutErrorCodes.ContractNotActive);
        return contract;
    }

    private async Task<bool> HasOpenRequestAsync(Guid contractId)
    {
        return await _requests.AnyAsync(x =>
            x.ContractId == contractId &&
            (x.Status == MoveOutRequestStatus.Pending ||
             x.Status == MoveOutRequestStatus.Approved));
    }

    private async Task<Guid> GetContractIdAsync(Guid requestId)
    {
        var query = await _requests.GetQueryableAsync();
        // Scalar projection ensures that the tracked request is first loaded
        // AFTER acquiring the shared Contract row lock.
        var contractId = await AsyncExecuter.FirstOrDefaultAsync(
            query.Where(x => x.Id == requestId).Select(x => x.ContractId));
        if (contractId == Guid.Empty)
            throw new BusinessException(MoveOutErrorCodes.NotFound);
        return contractId;
    }

    private async Task<MoveOutRequest> GetExistingAsync(Guid id)
    {
        var request = await _requests.FindAsync(id);
        if (request is null)
            throw new BusinessException(MoveOutErrorCodes.NotFound);
        return request;
    }

    private static void ValidateMoveOutDate(DateOnly submittedOn, DateOnly requestedOn)
    {
        if (!MoveOutNoticePolicy.IsMonthEnd(requestedOn))
            throw new BusinessException(MoveOutErrorCodes.InvalidMoveOutDate);

        if (requestedOn < MoveOutNoticePolicy.EarliestPermittedMoveOutDate(submittedOn))
            throw new BusinessException(MoveOutErrorCodes.InsufficientNotice)
                .WithData("EarliestMoveOutDate",
                    MoveOutNoticePolicy.EarliestPermittedMoveOutDate(submittedOn)
                        .ToString("yyyy-MM-dd"));
    }

    private static void ValidateOptionalText(string? value)
    {
        if (value?.Length > 1024)
            throw new BusinessException(MoveOutErrorCodes.TextTooLong);
    }

    private static MoveOutRequestDto ToDto(MoveOutRequest source) => new()
    {
        Id = source.Id,
        ContractId = source.ContractId,
        RoomId = source.RoomId,
        RequestedMoveOutDate = source.RequestedMoveOutDate,
        ApprovedMoveOutDate = source.ApprovedMoveOutDate,
        Reason = source.Reason,
        Status = source.Status,
        ReviewNotes = source.ReviewNotes,
        CreationTime = source.CreationTime,
        ReviewedAt = source.ReviewedAt,
        CancelledAt = source.CancelledAt,
        CompletedAt = source.CompletedAt
    };
}
