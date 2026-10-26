using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.Invoices;
using BuildingManagement.Utilities;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;

namespace BuildingManagement.MoveOuts;

/// <summary>
/// Actual read-only bridge across 2G.1 (request + checklist), 2G.2
/// (final meter evidence) and 2G.3 (draft evidence). It DOES NOT close a
/// contract, release a room, issue an invoice, record debt or move deposits.
/// Remains HTTP-disabled until building/operator authorization covers it.
/// </summary>
[RemoteService(IsEnabled = false)]
public sealed class MoveOutCompletionReadinessAppService : ApplicationService
{
    private readonly IRepository<MoveOutRequest, Guid> _requests;
    private readonly IRepository<MoveOutChecklistTask, Guid> _checklist;
    private readonly IRepository<Contract, Guid> _contracts;
    private readonly IRepository<Invoice, Guid> _invoices;
    private readonly IMoveOutFinalMeterReadinessAppService _meters;

    public MoveOutCompletionReadinessAppService(
        IRepository<MoveOutRequest, Guid> requests,
        IRepository<MoveOutChecklistTask, Guid> checklist,
        IRepository<Contract, Guid> contracts,
        IRepository<Invoice, Guid> invoices,
        IMoveOutFinalMeterReadinessAppService meters)
    {
        _requests = requests;
        _checklist = checklist;
        _contracts = contracts;
        _invoices = invoices;
        _meters = meters;
    }

    public async Task<MoveOutCompletionReadinessDto> GetAsync(Guid requestId)
    {
        if (requestId == Guid.Empty)
            throw new BusinessException(MoveOutErrorCodes.NotFound);

        var request = await _requests.FindAsync(requestId);
        if (request is null)
            throw new BusinessException(MoveOutErrorCodes.NotFound);

        var contract = await _contracts.FindAsync(request.ContractId);
        var tasks = await _checklist.GetListAsync(
            task => task.MoveOutRequestId == request.Id);
        var hasDraft = await _invoices.AnyAsync(invoice =>
            invoice.ContractId == request.ContractId &&
            invoice.RoomId == request.RoomId &&
            invoice.Status == InvoiceStatus.Draft);

        // A pending/rejected/cancelled request cannot produce valid
        // approved-date final meter evidence.
        var metersReady = false;
        if (request.Status == MoveOutRequestStatus.Approved)
        {
            var evidence = await _meters.GetAsync(request.Id);
            metersReady =
                evidence.MoveOutRequestId == request.Id &&
                evidence.ContractId == request.ContractId &&
                evidence.RoomId == request.RoomId &&
                evidence.HasReadingEvidenceForBothUtilities;
        }

        return MoveOutCompletionReadinessEvaluator.Assess(
            request, contract, tasks, metersReady, hasDraft);
    }
}

/// <summary>
/// Pure, fail-closed assessment: every checklist type must be present
/// exactly once, every verified completion must be durable, and the
/// financial stages must explicitly remain blocked while 2G.3/2G.4
/// cannot produce a settled invoice and an approved deposit disposition.
/// </summary>
public static class MoveOutCompletionReadinessEvaluator
{
    public static MoveOutCompletionReadinessDto Assess(
        MoveOutRequest request,
        Contract? contract,
        IReadOnlyCollection<MoveOutChecklistTask> tasks,
        bool hasFinalMeterEvidence,
        bool hasUtilityDraft)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tasks);

        var report = new MoveOutCompletionReadinessDto
        {
            MoveOutRequestId = request.Id,
            ContractId = request.ContractId,
            RoomId = request.RoomId,
            RequestStatus = request.Status,
            HasFinalMeterEvidence = hasFinalMeterEvidence,
            HasUtilityDraft = hasUtilityDraft,
            ChecklistTotal = tasks.Count,
            ChecklistCompleted = tasks.Count(x => x.IsCompleted)
        };

        if (request.Status != MoveOutRequestStatus.Approved ||
            !request.ApprovedMoveOutDate.HasValue)
            report.Blockers.Add("MoveOutNotApproved");

        if (contract is null || contract.Id != request.ContractId ||
            contract.RoomId != request.RoomId ||
            contract.Status != ContractStatus.Active)
            report.Blockers.Add("ActiveContractMismatch");

        var expected = Enum.GetValues<MoveOutChecklistTaskType>();
        foreach (var type in expected)
        {
            var matches = tasks.Where(x =>
                x.MoveOutRequestId == request.Id && x.Type == type).ToList();
            if (matches.Count != 1)
                report.Blockers.Add("ChecklistMissingOrDuplicate:" + type);
            else if (!matches[0].IsCompleted)
                report.Blockers.Add("ChecklistNotCompleted:" + type);
        }

        if (tasks.Count != expected.Length ||
            tasks.Any(x => x.MoveOutRequestId != request.Id ||
                           !Enum.IsDefined(x.Type)))
            report.Blockers.Add("ChecklistScopeInvalid");

        if (!hasFinalMeterEvidence)
            report.Blockers.Add("FinalMeterEvidenceMissing");

        if (!hasUtilityDraft)
            report.Blockers.Add("NoUtilityInvoiceDraft");

        // Draft is NOT payable/issued. There is presently no posted invoice
        // balance, verified final debt reconciliation or authorized
        // deposit settlement to trust. Do not infer any of these from a
        // deposit payment, checklist checkbox or utility draft.
        report.Blockers.Add("FinalInvoiceIssuanceNotImplemented");
        report.Blockers.Add("FinalDebtClearanceNotVerified");
        report.Blockers.Add("ContractDepositSettlementNotVerified");
        return report;
    }
}
