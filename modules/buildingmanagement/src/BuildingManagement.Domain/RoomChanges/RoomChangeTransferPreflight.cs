using System;
using System.Collections.Generic;
using System.Linq;
using BuildingManagement.Contracts;
using BuildingManagement.RoomReservations;
using BuildingManagement.Rooms;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// Phase 2H: policy-neutral, NON-MUTATING input gate for a future transfer.
/// The caller MUST load all facts inside an authorized, freshly locked,
/// same-building transaction before relying on this assessment.
/// No deposit moves, contracts end or rooms change in this class.
/// </summary>
public static class RoomChangeTransferPreflight
{
    public static RoomChangeTransferPreflightResult Assess(
        RoomChangeTransferFacts? facts)
    {
        if (facts is null)
            return new(false, new[] { "TransferFactsMissing" });

        var blockers = new List<string>();
        if (facts.SourceContractId == Guid.Empty ||
            facts.SourceRoomId == Guid.Empty ||
            facts.TargetRoomId == Guid.Empty ||
            facts.SourceBuildingId == Guid.Empty ||
            facts.TargetBuildingId == Guid.Empty ||
            facts.RequestedDate == default ||
            facts.SourceRoomId == facts.TargetRoomId)
            blockers.Add("InvalidTransferIdentityOrDate");

        if (facts.SourceBuildingId != facts.TargetBuildingId)
            blockers.Add("CrossBuildingTransferNotAuthorized");

        if (facts.SourceContractStatus != ContractStatus.Active ||
            facts.SourceRoomStatus != RoomStatus.Occupied)
            blockers.Add("SourceTenancyNotActive");

        // Never silently take a currently occupied, reserved, repairing or
        // inactive destination, irrespective of who requests the transfer.
        if (facts.TargetRoomStatus != RoomStatus.Available)
            blockers.Add("TargetRoomNotAvailable");

        if (string.IsNullOrWhiteSpace(facts.Reason) ||
            facts.Reason.Trim().Length is < 8 or > 500)
            blockers.Add("TransferReasonMissing");

        if (facts.TargetReservations is null || facts.TargetContracts is null)
            blockers.Add("TargetCommitmentFactsMissing");
        else
        {
            // A PendingPayment hold may be an active business negotiation.
            // Until its handling policy is approved, do not steal that room.
            if (facts.TargetReservations.Any(r => r is null ||
                r.Id == Guid.Empty || r.Status is
                    RoomReservationStatus.PendingPayment or
                    RoomReservationStatus.Reserved ||
                !Enum.IsDefined(r.Status)))
                blockers.Add("TargetReservationUnresolved");

            if (facts.TargetContracts.Any(c => c is null ||
                c.Id == Guid.Empty || c.Status is
                    ContractStatus.Active or ContractStatus.Signed ||
                !Enum.IsDefined(c.Status)))
                blockers.Add("TargetContractUnresolved");

            if (facts.TargetReservations.GroupBy(r => r?.Id).Any(g => g.Count() > 1) ||
                facts.TargetContracts.GroupBy(c => c?.Id).Any(g => g.Count() > 1))
                blockers.Add("DuplicateTargetCommitmentEvidence");
        }

        if (!facts.TenantRosterVerified)
            blockers.Add("TransferTenantRosterNotVerified");
        if (!facts.FinalMeterEvidenceVerified || !facts.SourceInspectionSigned)
            blockers.Add("SourceHandoverEvidenceNotVerified");
        if (!facts.SourceInvoiceDebtSettled)
            blockers.Add("SourceDebtNotReconciled");

        // OI-07 remains a BUSINESS DECISION, not a code default.
        // A successful preflight is NOT a transfer authorization or a
        // substitute for an atomic, audited carry-forward execution.
        if (!facts.ContractTreatmentPolicyApproved)
            blockers.Add("ContractTreatmentPolicyUnapproved");
        if (!facts.MidMonthPricingPolicyApproved)
            blockers.Add("TransferPricingPolicyUnapproved");
        if (!facts.DepositCarryForwardPolicyApproved ||
            !facts.DepositExclusivityReconciled)
            blockers.Add("DepositCarryForwardNotApprovedOrReconciled");

        return new(blockers.Count == 0, blockers.AsReadOnly());
    }
}

public sealed record RoomChangeTransferFacts(
    Guid SourceContractId,
    Guid SourceRoomId,
    Guid TargetRoomId,
    Guid SourceBuildingId,
    Guid TargetBuildingId,
    DateOnly RequestedDate,
    string? Reason,
    ContractStatus SourceContractStatus,
    RoomStatus SourceRoomStatus,
    RoomStatus TargetRoomStatus,
    IReadOnlyCollection<RoomChangeTargetReservationFact>? TargetReservations,
    IReadOnlyCollection<RoomChangeTargetContractFact>? TargetContracts,
    bool TenantRosterVerified,
    bool FinalMeterEvidenceVerified,
    bool SourceInspectionSigned,
    bool SourceInvoiceDebtSettled,
    bool ContractTreatmentPolicyApproved,
    bool MidMonthPricingPolicyApproved,
    bool DepositCarryForwardPolicyApproved,
    bool DepositExclusivityReconciled);

public sealed record RoomChangeTargetReservationFact(
    Guid Id, RoomReservationStatus Status);

public sealed record RoomChangeTargetContractFact(
    Guid Id, ContractStatus Status);

public sealed record RoomChangeTransferPreflightResult(
    bool CanProceedToTransactionalReview,
    IReadOnlyList<string> Blockers);
