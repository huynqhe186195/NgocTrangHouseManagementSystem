using System;
using System.Collections.Generic;
using System.Linq;
using BuildingManagement.Contracts;
using Xunit;

namespace BuildingManagement.MoveOuts;

/// <summary>
/// Completion readiness is a read-only evidence report, never a financial
/// posting approval or a replacement for atomic CompleteMoveOut.
/// </summary>
public sealed class MoveOutCompletionReadinessEvaluator_Tests
{
    [Fact]
    public void CLOSECHECK01_Pending_Request_Has_No_Completion_Authority()
    {
        var (request, contract, tasks) = Make();
        var result = MoveOutCompletionReadinessEvaluator.Assess(
            request, contract, tasks, false, false);

        Assert.False(result.CanCompleteMoveOut);
        Assert.Contains("MoveOutNotApproved", result.Blockers);
        Assert.Contains("FinalMeterEvidenceMissing", result.Blockers);
        Assert.Contains("NoUtilityInvoiceDraft", result.Blockers);
        Assert.Contains("FinalInvoiceIssuanceNotImplemented", result.Blockers);
        Assert.Contains("FinalDebtClearanceNotVerified", result.Blockers);
        Assert.Contains("ContractDepositSettlementNotVerified", result.Blockers);
    }

    [Fact]
    public void CLOSECHECK02_Approved_But_Unchecked_Cannot_Close()
    {
        var (request, contract, tasks) = Make();
        request.Approve(DateTime.UtcNow, "Approved notice");

        var result = MoveOutCompletionReadinessEvaluator.Assess(
            request, contract, tasks, true, true);
        Assert.DoesNotContain("MoveOutNotApproved", result.Blockers);
        Assert.Equal(5, result.ChecklistTotal);
        Assert.Equal(0, result.ChecklistCompleted);
        foreach (var type in Enum.GetValues<MoveOutChecklistTaskType>())
            Assert.Contains("ChecklistNotCompleted:" + type, result.Blockers);
        Assert.True(result.HasFinalMeterEvidence);
        Assert.True(result.HasUtilityDraft);
        Assert.False(result.CanCompleteMoveOut);
    }

    [Fact]
    public void CLOSECHECK03_Approval_Does_Not_Equal_Issued_Invoice_Or_Paid_Deposit()
    {
        var (request, contract, tasks) = Make();
        request.Approve(DateTime.UtcNow, "Approved");

        var result = MoveOutCompletionReadinessEvaluator.Assess(
            request, contract, tasks, true, true);
        Assert.Contains("FinalInvoiceIssuanceNotImplemented", result.Blockers);
        Assert.Contains("FinalDebtClearanceNotVerified", result.Blockers);
        Assert.Contains("ContractDepositSettlementNotVerified", result.Blockers);
        Assert.False(result.CanCompleteMoveOut);
        Assert.Equal(ContractStatus.Active, contract.Status);
    }

    [Fact]
    public void CLOSECHECK04_Missing_Or_Duplicated_Checklist_Task_Is_Detected()
    {
        var (request, contract, tasks) = Make();
        request.Approve(DateTime.UtcNow, "Approved");
        tasks.RemoveAt(0);
        tasks.Add(new MoveOutChecklistTask(Guid.NewGuid(), request.Id,
            MoveOutChecklistTaskType.RoomInspection));
        var result = MoveOutCompletionReadinessEvaluator.Assess(
            request, contract, tasks, true, true);

        Assert.Contains("ChecklistMissingOrDuplicate:FinalMeterReading", result.Blockers);
        Assert.Contains("ChecklistMissingOrDuplicate:RoomInspection", result.Blockers);
        Assert.False(result.CanCompleteMoveOut);
    }

    [Fact]
    public void CLOSECHECK05_Other_Contract_Or_Room_Is_Rejected()
    {
        var (request, contract, tasks) = Make();
        request.Approve(DateTime.UtcNow, "Approved");
        var foreign = new Contract(Guid.NewGuid(), "NOT-MATCHING",
            Guid.NewGuid(), DateTime.UtcNow.Date, null, 1000000m);
        foreign.Activate();

        var result = MoveOutCompletionReadinessEvaluator.Assess(
            request, foreign, tasks, true, true);
        Assert.Contains("ActiveContractMismatch", result.Blockers);
        Assert.False(result.CanCompleteMoveOut);
    }

    [Fact]
    public void CLOSECHECK06_Wrong_Request_Checklist_Is_Invalid()
    {
        var (request, contract, tasks) = Make();
        request.Approve(DateTime.UtcNow, "Approved");
        var otherRequest = Guid.NewGuid();
        tasks[0] = new MoveOutChecklistTask(
            Guid.NewGuid(), otherRequest, MoveOutChecklistTaskType.FinalMeterReading);

        var result = MoveOutCompletionReadinessEvaluator.Assess(
            request, contract, tasks, true, true);
        Assert.Contains("ChecklistScopeInvalid", result.Blockers);
        Assert.Contains("ChecklistMissingOrDuplicate:FinalMeterReading", result.Blockers);
    }

    private static (MoveOutRequest Request, Contract Contract,
        List<MoveOutChecklistTask> Tasks) Make()
    {
        var room = Guid.NewGuid();
        var contract = new Contract(Guid.NewGuid(), "CLOSE-TEST",
            room, DateTime.UtcNow.Date, null, 1000000m);
        contract.Activate();
        var date = new DateOnly(2026, 12, 31);
        var request = new MoveOutRequest(
            Guid.NewGuid(), contract.Id, room, date, "Test only");
        var tasks = Enum.GetValues<MoveOutChecklistTaskType>()
            .Select(type => new MoveOutChecklistTask(
                Guid.NewGuid(), request.Id, type)).ToList();
        return (request, contract, tasks);
    }
}
