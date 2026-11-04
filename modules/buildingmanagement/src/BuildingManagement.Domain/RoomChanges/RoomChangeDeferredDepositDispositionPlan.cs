using System;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// Approved H4 outcome for any verified deposit surplus. These two choices
/// are mutually exclusive; neither choice is a bank payment or rent receipt.
/// </summary>
public enum RoomChangeDepositSurplusChoice
{
    None = 0,
    CreditNextMonthRent = 1,
    ReviewedManualRefund = 2
}

/// <summary>
/// A deterministic, non-posting H4 disposition proposal. The source/target
/// input must still be reconstructed from the authoritative, independently
/// reviewed cash-backed deposit journal under authenticated building scope.
/// The actual financial writer MUST persist distinct immutable receivable,
/// rent-credit/refund liability and bank-evidence rows in one transaction.
/// </summary>
public sealed record RoomChangeDeferredDepositDispositionPlan(
    Guid RequestId,
    Guid SourceContractId,
    Guid TargetContractId,
    DateOnly TransferDateVietnam,
    decimal SourceCashBackedHeldVnd,
    decimal AlreadyPostedApprovedDeductionsVnd,
    decimal CarriedTargetDepositVnd,
    decimal TargetRequiredDepositVnd,
    decimal OutstandingTopUpReceivableVnd,
    DateOnly? OutstandingTopUpDueDateVietnam,
    decimal NextMonthRentCreditLiabilityVnd,
    decimal ManualRefundLiabilityVnd,
    RoomChangeDepositSurplusChoice SurplusChoice)
{
    /// <summary>
    /// The Owner authorizes a move with an outstanding top-up.
    /// This planning flag is not permission for a database writer.
    /// </summary>
    public bool BusinessAllowsTransferWithUnpaidTopUp =>
        OutstandingTopUpReceivableVnd > 0m;

    public bool IsBankCashVerified => false;
    public bool IsFinancialJournalPosted => false;
    public bool IsRefundPaid => false;
    public bool IsRentCreditApplied => false;
    public bool CanExecuteRoomChange => false;
}

public static class RoomChangeDeferredDepositDispositionPlanner
{
    public static RoomChangeDeferredDepositDispositionPlan Plan(
        Guid requestId,
        RoomChangeDepositDifferenceFacts facts,
        DateOnly transferDateVietnam,
        RoomChangeDepositSurplusChoice surplusChoice,
        DateOnly? proposedTopUpDueDateVietnam = null)
    {
        if (requestId == Guid.Empty || facts is null ||
            transferDateVietnam == default ||
            transferDateVietnam.Year is < 2000 or > 2200 ||
            !Enum.IsDefined(surplusChoice))
            throw new InvalidOperationException(
                "An identified transfer, Vietnam civil date and exclusive surplus treatment are mandatory.");

        // Recompute the amount from original facts; never trust an altered
        // client-side preview record or claim that top-up cash already exists.
        var original = RoomChangeDepositDifferencePreviewCalculator.Calculate(facts);
        var topUp = original.UnpaidTopUpRequirementVnd;
        var surplus = original.UnpaidRefundCandidateVnd;
        var lastPermittedDueDate = transferDateVietnam.AddMonths(1);

        if ((surplus > 0m && surplusChoice == RoomChangeDepositSurplusChoice.None) ||
            (surplus == 0m && surplusChoice != RoomChangeDepositSurplusChoice.None) ||
            (topUp > 0m && surplusChoice != RoomChangeDepositSurplusChoice.None))
            throw new InvalidOperationException(
                "The approved surplus must choose exactly one future-rent credit or reviewed manual refund.");

        DateOnly? due = null;
        if (topUp > 0m)
        {
            due = proposedTopUpDueDateVietnam ?? lastPermittedDueDate;
            if (due.Value < transferDateVietnam ||
                due.Value > lastPermittedDueDate)
                throw new InvalidOperationException(
                    "Uncollected transfer deposit top-up must be due within one Vietnam calendar month.");
        }
        else if (proposedTopUpDueDateVietnam.HasValue)
        {
            throw new InvalidOperationException(
                "No deposit receivable or due date may be invented when there is no top-up.");
        }

        var credit = surplusChoice == RoomChangeDepositSurplusChoice.CreditNextMonthRent
            ? surplus : 0m;
        var refund = surplusChoice == RoomChangeDepositSurplusChoice.ReviewedManualRefund
            ? surplus : 0m;

        if (original.SourceAvailableToDispositionVnd !=
                original.ProposedCarryForwardVnd + credit + refund ||
            original.TargetRequiredHeldVnd !=
                original.ProposedCarryForwardVnd + topUp ||
            credit > 0m && refund > 0m)
            throw new InvalidOperationException(
                "Source deposit money cannot be allocated twice or counted as paid top-up.");

        return new RoomChangeDeferredDepositDispositionPlan(
            requestId, original.SourceContractId, original.TargetContractId,
            transferDateVietnam, original.SourceHeldCashVnd,
            original.AlreadyPostedApprovedDeductionsVnd,
            original.ProposedCarryForwardVnd, original.TargetRequiredHeldVnd,
            topUp, due, credit, refund, surplusChoice);
    }
}
