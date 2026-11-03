using System;
using System.IO;
using BuildingManagement.Rooms;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// H6: the reduced transfer checklist must have all ESSENTIAL signed
/// evidence, not five independent mutable booleans entered by the client.
/// This last transaction-local domain gate is called only after the server
/// has independently verified original E/W meter records, the complete
/// current roster, official source invoice/debt and actual cash-backed
/// deposit. It cannot verify bank records or human signatures by itself.
/// D06 detailed signer policy remains a separate business acceptance gate.
/// </summary>
public static class RoomChangeReducedHandoverChecklistGate
{
    public static void RequireForAtomicCompletion(
        RoomChangeReducedHandoverCompletionFacts? facts)
    {
        if (facts is null ||
            facts.Request is null || facts.Roster is null ||
            facts.Policy is null || facts.Review is null ||
            facts.Handover is null)
            throw new InvalidOperationException(
                "Signed transfer request, roster, policy and physical handover are required.");

        var request = facts.Request;
        var roster = facts.Roster;
        var policy = facts.Policy;
        var review = facts.Review;
        var handover = facts.Handover;

        // Revalidate the existing signed objects and cross-document scope.
        // The original meter source readings are verified in the caller,
        // where the live database facts can be reloaded and locked.
        try
        {
            roster.ReadVerifiedRoster();
            policy.Verify(request, roster);
            review.Verify(policy, request, roster);
        }
        catch (Exception ex) when (ex is InvalidDataException or
            InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException(
                "Signed policy or full tenant-roster evidence has drifted.", ex);
        }

        if (request.Id == Guid.Empty ||
            request.SourceContractId == Guid.Empty ||
            request.SourceRoomId == Guid.Empty ||
            request.TargetRoomId == Guid.Empty ||
            request.SourceRoomId == request.TargetRoomId ||
            request.BuildingId == Guid.Empty ||
            request.RequestedDate == default ||
            request.Status is not (RoomChangeRequestStatus.Pending or
                RoomChangeRequestStatus.PolicyHold) ||
            handover.RequestId != request.Id ||
            handover.SourceContractId != request.SourceContractId ||
            handover.SourceRoomId != request.SourceRoomId ||
            handover.BuildingId != request.BuildingId ||
            handover.HandoverDate != request.RequestedDate ||
            handover.ElectricityMeterId == Guid.Empty ||
            handover.WaterMeterId == Guid.Empty ||
            handover.ElectricityMeterId == handover.WaterMeterId ||
            handover.ElectricityReadingId == Guid.Empty ||
            handover.WaterReadingId == Guid.Empty ||
            handover.ElectricityReadingId == handover.WaterReadingId ||
            handover.AttestedByUserId == Guid.Empty ||
            handover.AttestedAtUtc.Kind != DateTimeKind.Utc ||
            handover.EvidenceSha256.Length != 64 ||
            !Reference(handover.InspectionEvidenceReference) ||
            !Reference(handover.SignedHandoverReference) ||
            roster.RoomChangeRequestId != request.Id ||
            roster.SourceContractId != request.SourceContractId ||
            roster.SourceRoomId != request.SourceRoomId ||
            roster.BuildingId != request.BuildingId ||
            roster.RosterCount == 0 ||
            policy.ContractTreatment != RoomChangeContractTreatment.NewContract ||
            policy.TenantTreatment != RoomChangeTenantTreatment.AllCurrentTenants ||
            !review.Accepted ||
            review.RequestId != request.Id ||
            review.BuildingId != request.BuildingId ||
            !facts.ServerVerifiedOriginalElectricityWaterReadings ||
            !facts.ServerVerifiedExactFullTenantRoster ||
            !facts.ServerVerifiedAllOfficialSourceReceivablesSettled ||
            !facts.ServerVerifiedActualCashBackedHeldDeposit ||
            facts.SourceRoomDisposition is not
                (RoomStatus.Available or RoomStatus.Reserved or RoomStatus.Maintenance) ||
            (facts.SourceRoomDisposition == RoomStatus.Reserved) !=
                facts.HasConfirmedFutureSourceReservation ||
            (facts.SourceRoomDisposition == RoomStatus.Maintenance &&
                facts.HasConfirmedFutureSourceReservation) ||
            !Reference(facts.SourceDispositionEvidenceReference) ||
            !Reference(facts.CompletionReference) ||
            facts.CompletedByUserId == Guid.Empty ||
            facts.CompletedAtUtc.Kind != DateTimeKind.Utc ||
            facts.CompletedAtUtc < request.CreatedAtUtc ||
            facts.CompletedAtUtc < roster.CapturedAtUtc ||
            facts.CompletedAtUtc < review.ReviewedAtUtc ||
            facts.CompletedAtUtc < handover.AttestedAtUtc)
            throw new InvalidOperationException(
                "Reduced H6 handover is missing verified meter, inspection, " +
                "financial, roster, booking or signed completion evidence.");
    }

    private static bool Reference(string? reference)
        => !string.IsNullOrWhiteSpace(reference) &&
           reference.Trim().Length is >= 12 and <= 500;
}

/// <summary>
/// Server-side facts only; NEVER expose this record as an HTTP body or accept
/// the ServerVerified* flags from a caller. Inputs must be derived under the
/// existing SERIALIZABLE UoW and live membership/room locks. This gate is
/// defense-in-depth, not a replacement for those database verifiers.
/// </summary>
public sealed record RoomChangeReducedHandoverCompletionFacts(
    RoomChangeRequest Request,
    RoomChangeTenantRosterSnapshotRecord Roster,
    RoomChangeBusinessPolicyRecord Policy,
    RoomChangeBusinessPolicyReviewRecord Review,
    RoomChangeHandoverEvidenceRecord Handover,
    RoomStatus SourceRoomDisposition,
    bool HasConfirmedFutureSourceReservation,
    string? SourceDispositionEvidenceReference,
    string? CompletionReference,
    Guid CompletedByUserId,
    DateTime CompletedAtUtc,
    bool ServerVerifiedOriginalElectricityWaterReadings,
    bool ServerVerifiedExactFullTenantRoster,
    bool ServerVerifiedAllOfficialSourceReceivablesSettled,
    bool ServerVerifiedActualCashBackedHeldDeposit);
