using System;
using System.Collections.Generic;
using System.Linq;
using BuildingManagement.Contracts;
using BuildingManagement.MoveOuts;
using BuildingManagement.Payments;

namespace BuildingManagement.DepositSettlements;

/// <summary>
/// Read-only audit of existing contract-deposit allocations and dispositions.
/// Neither a payment's Unapplied marker nor the contract's promised deposit
/// is, by itself, proof of money currently held. No refund or offset approval.
/// </summary>
public sealed class ContractDepositLedgerReview
{
    public Guid MoveOutRequestId { get; init; }
    public Guid ContractId { get; init; }
    public decimal? VerifiedIncomingAllocationTotal { get; internal set; }
    public decimal? RecordedCompletedOutgoingTotal { get; internal set; }
    public int CompletedIncomingCount { get; internal set; }
    public List<string> Blockers { get; } = new();

    // Adjustment priorities, payment provenance across transfers, and owner
    // refund approval remain unresolved. Never present a final cash balance.
    public decimal? RefundableBalance => null;
    public bool CanRefund => false;
    public bool CanOffsetDebt => false;
    public bool IsSettlementApproved => false;
}

public static class ContractDepositLedgerReviewEvaluator
{
    public static ContractDepositLedgerReview Assess(
        MoveOutRequest request, Contract? contract,
        IReadOnlyCollection<DepositSettlement> relatedSettlements,
        IReadOnlyCollection<Payment> linkedPayments)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(relatedSettlements);
        ArgumentNullException.ThrowIfNull(linkedPayments);

        var result = new ContractDepositLedgerReview
        {
            MoveOutRequestId = request.Id, ContractId = request.ContractId
        };
        if (request.Status != MoveOutRequestStatus.Approved ||
            !request.ApprovedMoveOutDate.HasValue ||
            contract is null ||
            contract.Id != request.ContractId ||
            contract.RoomId != request.RoomId ||
            contract.Status != ContractStatus.Active)
            result.Blockers.Add("ApprovedMoveOutAndActiveContractRequired");

        var settlements = relatedSettlements.ToArray();
        var payments = linkedPayments.ToArray();
        if (settlements.Any(x => x is null || x.Id == Guid.Empty ||
            !Enum.IsDefined(x.Type) || !Enum.IsDefined(x.Status) ||
            x.Amount <= 0 ||
            (x.Status == DepositSettlementStatus.Completed &&
             !x.CompletedAt.HasValue)) ||
            settlements.Select(x => x.Id).Distinct().Count() != settlements.Length ||
            payments.Any(x => x is null || x.Id == Guid.Empty ||
                x.IsDeleted || x.Amount <= 0 || !Enum.IsDefined(x.Status)) ||
            payments.Select(x => x.Id).Distinct().Count() != payments.Length)
        {
            result.Blockers.Add("LedgerEvidenceInvalid");
            Finish(result);
            return result;
        }

        var incoming = settlements.Where(x =>
            x.TargetContractId == request.ContractId &&
            x.Type is DepositSettlementType.AllocateToContractDeposit or
                DepositSettlementType.ContractTransfer or
                DepositSettlementType.RoomChangeCarryForward).ToArray();
        var outgoing = settlements.Where(x =>
            x.SourceContractId == request.ContractId &&
            x.Type is DepositSettlementType.Refund or
                DepositSettlementType.Forfeit or
                DepositSettlementType.ContractTransfer or
                DepositSettlementType.RoomChangeCarryForward or
                DepositSettlementType.RentCredit).ToArray();

        if (settlements.Any(x => x.Status == DepositSettlementStatus.Pending &&
            (x.TargetContractId == request.ContractId ||
             x.SourceContractId == request.ContractId)))
            result.Blockers.Add("PendingDepositSettlementRequiresReview");

        var verified = incoming.Where(x =>
            x.Status == DepositSettlementStatus.Completed &&
            x.Type == DepositSettlementType.AllocateToContractDeposit).ToArray();

        if (incoming.Any(x => x.Type !=
                DepositSettlementType.AllocateToContractDeposit))
            result.Blockers.Add("IncomingTransferProvenanceNotVerified");

        if (verified.Length == 0)
            result.Blockers.Add("NoCompletedDepositAllocation");

        var paymentMap = payments.ToDictionary(x => x.Id);
        foreach (var allocation in verified)
        {
            if (allocation.PaymentId is not Guid pid ||
                !paymentMap.TryGetValue(pid, out var payment) ||
                payment.Status != PaymentStatus.Completed ||
                !payment.PaidAt.HasValue ||
                payment.DepositDisposition is not
                    (DepositDisposition.Unapplied or
                     DepositDisposition.AppliedToContract) ||
                (payment.Purpose != PaymentPurpose.ContractDeposit &&
                 payment.Purpose != PaymentPurpose.ReservationDeposit) ||
                (payment.Purpose == PaymentPurpose.ContractDeposit &&
                 (payment.ContractId != request.ContractId ||
                  allocation.SourceRoomReservationId.HasValue)) ||
                (payment.Purpose == PaymentPurpose.ReservationDeposit &&
                 (!payment.RoomReservationId.HasValue ||
                  payment.RoomReservationId != allocation.SourceRoomReservationId)))
            {
                result.Blockers.Add("DepositAllocationPaymentMismatch");
                break;
            }
        }

        // All settlements referencing each payment were fetched, including
        // other target contracts. Detect double-application globally.
        foreach (var grouping in settlements
            .Where(x => x.Type == DepositSettlementType.AllocateToContractDeposit &&
                        x.Status == DepositSettlementStatus.Completed &&
                        x.PaymentId.HasValue)
            .GroupBy(x => x.PaymentId!.Value))
        {
            if (!paymentMap.TryGetValue(grouping.Key, out var payment) ||
                grouping.Sum(x => x.Amount) > payment.Amount ||
                grouping.Select(x => x.TargetContractId).Distinct().Count() != 1)
            {
                result.Blockers.Add("DepositPaymentOverAllocatedOrCrossContract");
                break;
            }
        }

        if (outgoing.Length != 0)
            result.Blockers.Add("RecordedOutgoingDepositNeedsReconciliation");

        if (contract is not null &&
            verified.Sum(x => x.Amount) > contract.DepositAmount)
            result.Blockers.Add("AllocatedDepositExceedsContractRequirement");

        if (!result.Blockers.Any())
        {
            result.VerifiedIncomingAllocationTotal =
                verified.Sum(x => x.Amount);
            result.CompletedIncomingCount = verified.Length;
            result.RecordedCompletedOutgoingTotal = 0m;
        }

        Finish(result);
        return result;
    }

    private static void Finish(ContractDepositLedgerReview review)
    {
        // These must stay present even when the allocation ledger is clean.
        review.Blockers.Add("FinalInvoiceDebtNotReconciled");
        review.Blockers.Add("DepositDeductionPolicyNotApproved");
        review.Blockers.Add("RefundAuthorizationAndHandoverMissing");
    }
}
