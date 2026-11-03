using System;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// H4 transfer deposit arithmetic PREVIEW ONLY. Inputs must be reconstructed
/// by an authenticated, independently reviewed cash-ledger adapter: reference
/// text alone does not prove receipt, owner approval, offset or bank execution.
/// Does not post a top-up, carry-forward, refund or new-contract deposit.
/// </summary>
public static class RoomChangeDepositDifferencePreviewCalculator
{
    public static RoomChangeDepositDifferencePreview Calculate(
        RoomChangeDepositDifferenceFacts? facts)
    {
        if (facts is null ||
            facts.SourceContractId == Guid.Empty ||
            facts.TargetContractId == Guid.Empty ||
            facts.SourceContractId == facts.TargetContractId ||
            !PositiveWholeVnd(facts.SourceCashBackedHeldDepositVnd) ||
            !PositiveWholeVnd(facts.TargetContractRequiredDepositVnd) ||
            !NonnegativeWholeVnd(facts.AlreadyPostedApprovedDeductionsVnd) ||
            facts.AlreadyPostedApprovedDeductionsVnd >
                facts.SourceCashBackedHeldDepositVnd ||
            !Reference(facts.SourceHeldCashLedgerReference) ||
            !Reference(facts.TargetRequiredDepositPolicyReference))
            throw new InvalidOperationException(
                "Deposit preview requires separate source/target contracts, source cash proof and whole-VND requirements.");

        if (facts.HasUnresolvedDispute || facts.HasEarlierRefundOrCarryForward)
            throw new InvalidOperationException(
                "Unresolved dispute or prior outgoing deposit disposition blocks any new transfer preview.");

        if (facts.AlreadyPostedApprovedDeductionsVnd > 0m)
        {
            if (!Reference(facts.OwnerApprovedDeductionReference))
                throw new InvalidOperationException(
                    "Posted deductions require a separate Owner-approved financial reference.");
        }
        else if (facts.OwnerApprovedDeductionReference is not null)
        {
            throw new InvalidOperationException(
                "No fictitious deduction evidence is permitted with zero deductions.");
        }

        decimal remaining;
        decimal carry;
        decimal topUp;
        decimal refundable;
        try
        {
            remaining = checked(facts.SourceCashBackedHeldDepositVnd -
                                facts.AlreadyPostedApprovedDeductionsVnd);
            carry = Math.Min(remaining, facts.TargetContractRequiredDepositVnd);
            topUp = checked(facts.TargetContractRequiredDepositVnd - carry);
            refundable = checked(remaining - carry);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException("Deposit preview arithmetic overflow.", ex);
        }

        if (remaining != carry + refundable ||
            facts.TargetContractRequiredDepositVnd != carry + topUp ||
            (topUp > 0m && refundable > 0m))
            throw new InvalidOperationException("Deposit preview accounting invariant failed.");

        var lane = topUp > 0m
            ? RoomChangeDepositTreatment.CarryWithVerifiedTopUp
            : refundable > 0m
                ? RoomChangeDepositTreatment.PartialCarryWithRefund
                : RoomChangeDepositTreatment.CarryAllWithNoDifference;

        return new RoomChangeDepositDifferencePreview(
            facts.SourceContractId,
            facts.TargetContractId,
            facts.SourceCashBackedHeldDepositVnd,
            facts.AlreadyPostedApprovedDeductionsVnd,
            remaining,
            facts.TargetContractRequiredDepositVnd,
            carry,
            topUp,
            refundable,
            lane,
            facts.SourceHeldCashLedgerReference.Trim(),
            facts.TargetRequiredDepositPolicyReference.Trim());
    }

    private static bool PositiveWholeVnd(decimal amount)
        => amount > 0m && decimal.Truncate(amount) == amount;

    private static bool NonnegativeWholeVnd(decimal amount)
        => amount >= 0m && decimal.Truncate(amount) == amount;

    private static bool Reference(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Trim().Length is >= 12 and <= 500;
}

public sealed record RoomChangeDepositDifferenceFacts(
    Guid SourceContractId,
    Guid TargetContractId,
    decimal SourceCashBackedHeldDepositVnd,
    decimal AlreadyPostedApprovedDeductionsVnd,
    decimal TargetContractRequiredDepositVnd,
    string? SourceHeldCashLedgerReference,
    string? TargetRequiredDepositPolicyReference,
    string? OwnerApprovedDeductionReference,
    bool HasUnresolvedDispute,
    bool HasEarlierRefundOrCarryForward);

/// <summary>
/// Candidate split after already POSTED approved source deductions.
/// TopUp is a FUTURE collection required, NOT cash received.
/// Refund is a FUTURE payout candidate, NOT funds disbursed.
/// The source cash must be validated independently before considering
/// any authoritative transfer writer. No cash movement is authorized here.
/// </summary>
public sealed record RoomChangeDepositDifferencePreview(
    Guid SourceContractId,
    Guid TargetContractId,
    decimal SourceHeldCashVnd,
    decimal AlreadyPostedApprovedDeductionsVnd,
    decimal SourceAvailableToDispositionVnd,
    decimal TargetRequiredHeldVnd,
    decimal ProposedCarryForwardVnd,
    decimal UnpaidTopUpRequirementVnd,
    decimal UnpaidRefundCandidateVnd,
    RoomChangeDepositTreatment ProposedPolicyLane,
    string SourceCashLedgerReference,
    string TargetDepositPolicyReference)
{
    public bool IsCashMovementPosted => false;
    public bool IsIndependentlyApprovedExecution => false;
    public bool CanExecuteRoomChange => false;
}
