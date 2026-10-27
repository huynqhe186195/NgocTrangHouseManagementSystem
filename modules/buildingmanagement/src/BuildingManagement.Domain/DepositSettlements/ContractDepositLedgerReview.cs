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
    // Completed direct receipts are OBSERVED cash, not an approved held
    // balance or evidence of a completed DepositSettlement.
    public decimal? ObservedCompletedContractDepositReceiptsTotal { get; internal set; }
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

        var completedContractReceipts = payments.Where(x =>
            x.Purpose == PaymentPurpose.ContractDeposit &&
            x.ContractId == request.ContractId &&
            x.Status == PaymentStatus.Completed).ToArray();
        result.ObservedCompletedContractDepositReceiptsTotal =
            completedContractReceipts.Sum(x => x.Amount);

        // The existing completion workflow enforces unique transaction
        // references; preserve that check when auditing legacy/imported
        // records which did not pass through the normal workflow.
        var paymentReferences = payments
            .Where(x => x.Status == PaymentStatus.Completed &&
                        !string.IsNullOrWhiteSpace(x.TransactionReference))
            .Select(x => x.TransactionReference!.Trim()).ToArray();
        if (paymentReferences.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            paymentReferences.Length)
            result.Blockers.Add("DuplicateDepositTransactionReference");

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

        // Direct contract deposit payments are normally fully journaled
        // at completion. A missing or partial allocation must never be
        // reported as cleared/held, even when no journal references it.
        if (completedContractReceipts.Any(payment =>
                verified.Where(x => x.PaymentId == payment.Id)
                    .Sum(x => x.Amount) != payment.Amount))
            result.Blockers.Add("CompletedContractDepositNotFullyAllocated");

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

        // An allocation and a reservation refund/forfeit can reference the
        // SAME completed deposit Payment, even when the refund is not
        // directly linked to this contract. The reader has loaded all
        // settlement rows sharing each allocated PaymentId. Without this
        // second cross-type check a single source receipt can be disposed
        // twice while each individual allocation looks valid.
        var allocatedPaymentIds = verified
            .Where(x => x.PaymentId.HasValue)
            .Select(x => x.PaymentId!.Value).ToHashSet();
        foreach (var group in settlements
            .Where(x => x.PaymentId.HasValue &&
                        allocatedPaymentIds.Contains(x.PaymentId.Value) &&
                        x.Type is DepositSettlementType.AllocateToContractDeposit or
                            DepositSettlementType.Refund or
                            DepositSettlementType.Forfeit)
            .GroupBy(x => x.PaymentId!.Value))
        {
            var completed = group.Where(x =>
                x.Status == DepositSettlementStatus.Completed).ToArray();
            if (paymentMap.TryGetValue(group.Key, out var payment) &&
                completed.Sum(x => x.Amount) > payment.Amount)
                result.Blockers.Add("DepositPaymentDoubleDisposed");

            if (completed.Any(x =>
                    x.Type is DepositSettlementType.Refund or
                        DepositSettlementType.Forfeit) &&
                completed.Any(x =>
                    x.Type == DepositSettlementType.AllocateToContractDeposit))
                result.Blockers.Add("MixedDepositDispositionRequiresReview");

            if (group.Any(x => x.Status == DepositSettlementStatus.Pending))
                result.Blockers.Add("PendingDepositPaymentDispositionRequiresReview");
        }

        // Outgoing amount cannot exceed confirmed completed allocations.
        // Incoming transfer provenance has not been verified, so this is
        // an anomaly indicator, NOT a final cash-balance calculation.
        if (outgoing.Where(x => x.Status == DepositSettlementStatus.Completed)
                .Sum(x => x.Amount) > verified.Sum(x => x.Amount))
            result.Blockers.Add("RecordedOutgoingExceedsVerifiedIncoming");

        // Legacy or externally modified forfeiture rows can bypass the
        // current DepositSettlement constructor. Audit must still surface
        // an undocumented deduction even if it was already marked Complete.
        if (outgoing.Any(x => x.Type == DepositSettlementType.Forfeit &&
                              string.IsNullOrWhiteSpace(x.Notes)))
            result.Blockers.Add("ContractDepositForfeitReasonMissing");

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
