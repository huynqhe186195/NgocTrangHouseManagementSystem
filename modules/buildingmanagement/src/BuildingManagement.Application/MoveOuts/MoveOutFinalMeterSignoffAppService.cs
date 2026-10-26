using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.Security;
using BuildingManagement.Utilities;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;
using Volo.Abp.Timing;
using Volo.Abp.Uow;

namespace BuildingManagement.MoveOuts;

/// <summary>
/// Persisted final-meter checklist signoff; no other checklist type is
/// completable through this service. The current authorization policy is
/// fail-closed until reviewed building grants are enabled.
/// </summary>
[RemoteService(IsEnabled = false)]
public class MoveOutFinalMeterSignoffAppService : ApplicationService
{
    private readonly IRepository<MoveOutRequest, Guid> _requests;
    private readonly IRepository<MoveOutChecklistTask, Guid> _checklist;
    private readonly IContractRepository _contractLocks;
    private readonly IBuildingCrudAccessPolicy _scope;
    private readonly IMoveOutFinalMeterReadinessAppService _meterEvidence;
    private readonly IClock _clock;
    private readonly IAsyncQueryableExecuter _queryExecuter;

    public MoveOutFinalMeterSignoffAppService(
        IRepository<MoveOutRequest, Guid> requests,
        IRepository<MoveOutChecklistTask, Guid> checklist,
        IContractRepository contractLocks,
        IBuildingCrudAccessPolicy scope,
        IMoveOutFinalMeterReadinessAppService meterEvidence,
        IClock clock,
        IAsyncQueryableExecuter queryExecuter)
    {
        _requests = requests;
        _checklist = checklist;
        _contractLocks = contractLocks;
        _scope = scope;
        _meterEvidence = meterEvidence;
        _clock = clock;
        _queryExecuter = queryExecuter ?? throw new ArgumentNullException(nameof(queryExecuter));
    }

    [UnitOfWork(isTransactional: true)]
    public virtual async Task<MoveOutChecklistTaskDto> VerifyAsync(Guid requestId)
    {
        if (requestId == Guid.Empty)
            throw new BusinessException(MoveOutErrorCodes.NotFound);

        // Lock order is Contract -> MoveOut request -> checklist.
        // Resolve the scalar key before tracking the request.
        var query = await _requests.GetQueryableAsync();
        var contractId = await _queryExecuter.FirstOrDefaultAsync(
            query.Where(x => x.Id == requestId).Select(x => x.ContractId));
        if (contractId == Guid.Empty)
            throw new BusinessException(MoveOutErrorCodes.NotFound);

        var contract = await _contractLocks.FindForUpdateAsync(contractId);
        if (contract is null || contract.Status != ContractStatus.Active)
            throw new BusinessException(MoveOutErrorCodes.ContractNotActive);

        var request = await _requests.FindAsync(requestId);
        if (request is null ||
            request.ContractId != contract.Id ||
            request.RoomId != contract.RoomId ||
            request.Status != MoveOutRequestStatus.Approved ||
            !request.ApprovedMoveOutDate.HasValue)
            throw new BusinessException(MoveOutErrorCodes.FinalMeterSignoffNotAllowed);

        // This is a room-sensitive write; a caller with the request GUID
        // alone must NOT be allowed to change this checklist. The staged
        // policy currently denies every live caller until building/role
        // grants are reviewed and registered.
        await _scope.EnsureResourceAsync(
            BuildingBusinessResourceKind.Room, request.RoomId,
            BuildingBusinessCapability.ManageRoom);

        var candidates = await _checklist.GetListAsync(x =>
            x.MoveOutRequestId == requestId &&
            x.Type == MoveOutChecklistTaskType.FinalMeterReading);
        if (candidates.Count != 1)
            throw new BusinessException(MoveOutErrorCodes.FinalMeterChecklistMissing);

        var task = candidates[0];
        if (task.IsCompleted)
            return ToDto(task); // Idempotent retry, no duplicate audit writes.

        var evidence = await _meterEvidence.GetAsync(requestId);
        if (!HasValidFinalMeterEvidence(request, evidence))
            throw new BusinessException(MoveOutErrorCodes.FinalMeterEvidenceInvalid);

        task.MarkFinalMeterReadingsVerified(_clock.Now);
        await _checklist.UpdateAsync(task, autoSave: true);
        return ToDto(task);
    }

    private static bool HasValidFinalMeterEvidence(
        MoveOutRequest request, MoveOutFinalMeterReadinessDto evidence)
    {
        if (evidence.MoveOutRequestId != request.Id ||
            evidence.ContractId != request.ContractId ||
            evidence.RoomId != request.RoomId ||
            evidence.RequestStatus != MoveOutRequestStatus.Approved ||
            evidence.ApprovedMoveOutDate != request.ApprovedMoveOutDate ||
            evidence.Utilities.Count != 2 ||
            !evidence.HasReadingEvidenceForBothUtilities)
            return false;

        var types = evidence.Utilities.Select(x => x.UtilityType).ToArray();
        if (!types.Contains(UtilityType.Electricity) ||
            !types.Contains(UtilityType.Water) ||
            types.Distinct().Count() != 2)
            return false;

        return evidence.Utilities.All(x =>
            x.Status == "ReadingEvidencePresent" &&
            x.UtilityMeterId.HasValue &&
            x.UtilityMeterId.Value != Guid.Empty &&
            x.MeterReadingId.HasValue &&
            x.MeterReadingId.Value != Guid.Empty &&
            x.FinalReading.HasValue &&
            x.FinalReading.Value >= 0m &&
            x.CoverageIssues is { Count: 0 });
    }

    private static MoveOutChecklistTaskDto ToDto(MoveOutChecklistTask task)
        => new()
        {
            Id = task.Id,
            MoveOutRequestId = task.MoveOutRequestId,
            Type = task.Type,
            IsCompleted = task.IsCompleted,
            CompletedAt = task.CompletedAt
        };
}
