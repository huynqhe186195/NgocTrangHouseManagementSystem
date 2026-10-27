using System;
using System.Collections.Generic;
using System.Linq;

namespace BuildingManagement.Invoices;

/// <summary>
/// Facts MUST come from fresh authenticated server-side queries performed
/// INSIDE the same transaction and lock boundary as any future posting.
/// A browser DTO, a cached preflight report or a previous decision is not
/// an authorization grant. This policy grants no invoice mutation.
/// </summary>
public sealed record InvoiceIssuanceScopeFacts(
    Guid? AuthenticatedIssuerId,
    bool IssuerPermissionGranted,
    Guid RequestedMoveOutId,
    Guid PersistedMoveOutId,
    Guid RequestedDraftInvoiceId,
    Guid PersistedDraftInvoiceId,
    Guid InvoiceContractId,
    Guid InvoiceRoomId,
    Guid ContractId,
    Guid ContractRoomId,
    Guid RoomId,
    Guid RoomFloorId,
    Guid FloorId,
    Guid FloorBuildingId,
    Guid BuildingId,
    IReadOnlyCollection<Guid>? IssuerBuildingMemberships,
    InvoiceStatus PersistedInvoiceStatus,
    string? PersistedEvidenceSha256,
    bool ExistingIssuedInvoiceForPeriod);

/// <summary>
/// Only indicates whether identity/resource relationships appear consistent
/// for further server-side transaction design. NEVER authorizes Issue/Post.
/// </summary>
public sealed class InvoiceIssuanceScopeReview
{
    public List<string> Blockers { get; } = new();
    // This flag refers ONLY to graph/identity/evidence consistency, not
    // issuance readiness. Financial prerequisites are checked separately.
    public bool IsScopeConsistent => Blockers.All(
        x => x == "FinancialIssuancePrerequisitesIncomplete");
    public bool CanIssue => false;
    public bool CanPostReceivable => false;
}

public static class InvoiceIssuanceScopePolicy
{
    public static InvoiceIssuanceScopeReview Assess(
        FinalInvoiceIssuePreflight? preflight,
        InvoiceIssuanceScopeFacts? facts)
    {
        var result = new InvoiceIssuanceScopeReview();

        if (preflight is null || facts is null)
        {
            result.Blockers.Add("IssuerScopeFactsUnavailable");
            return result;
        }

        if (!facts.AuthenticatedIssuerId.HasValue ||
            facts.AuthenticatedIssuerId.Value == Guid.Empty ||
            !facts.IssuerPermissionGranted ||
            facts.IssuerBuildingMemberships is null ||
            facts.BuildingId == Guid.Empty ||
            !facts.IssuerBuildingMemberships.Contains(facts.BuildingId))
            result.Blockers.Add("IssuerIdentityOrBuildingPermissionDenied");

        // Every link must resolve from the same persisted graph. A caller
        // must never be able to choose a different invoice, contract, room,
        // floor, building or move-out request to borrow someone else's grant.
        if (facts.RequestedMoveOutId == Guid.Empty ||
            facts.PersistedMoveOutId == Guid.Empty ||
            facts.RequestedMoveOutId != facts.PersistedMoveOutId ||
            facts.RequestedMoveOutId != preflight.MoveOutRequestId ||
            facts.RequestedDraftInvoiceId == Guid.Empty ||
            facts.PersistedDraftInvoiceId == Guid.Empty ||
            facts.RequestedDraftInvoiceId != facts.PersistedDraftInvoiceId ||
            facts.RequestedDraftInvoiceId != preflight.UtilityDraftId ||
            facts.InvoiceContractId == Guid.Empty ||
            facts.InvoiceRoomId == Guid.Empty ||
            facts.ContractId == Guid.Empty ||
            facts.ContractRoomId == Guid.Empty ||
            facts.RoomId == Guid.Empty ||
            facts.RoomFloorId == Guid.Empty ||
            facts.FloorId == Guid.Empty ||
            facts.FloorBuildingId == Guid.Empty ||
            facts.InvoiceContractId != facts.ContractId ||
            facts.ContractId != preflight.ContractId ||
            facts.InvoiceRoomId != facts.RoomId ||
            facts.ContractRoomId != facts.RoomId ||
            facts.RoomId != preflight.RoomId ||
            facts.RoomFloorId != facts.FloorId ||
            facts.FloorBuildingId != facts.BuildingId)
            result.Blockers.Add("IssuerResourceRelationshipMismatch");

        if (facts.PersistedInvoiceStatus != InvoiceStatus.Draft ||
            !Enum.IsDefined(facts.PersistedInvoiceStatus))
            result.Blockers.Add("PersistedInvoiceNotDraft");

        if (preflight.KnownBaseChargeTotal is null ||
            preflight.BaseCharges.Count != 3 ||
            string.IsNullOrWhiteSpace(preflight.EvidenceSha256) ||
            string.IsNullOrWhiteSpace(facts.PersistedEvidenceSha256) ||
            !StringComparer.Ordinal.Equals(
                preflight.EvidenceSha256,
                facts.PersistedEvidenceSha256) ||
            !IsSha256(facts.PersistedEvidenceSha256))
            result.Blockers.Add("PersistedInvoiceEvidenceMismatch");

        if (facts.ExistingIssuedInvoiceForPeriod)
            result.Blockers.Add("InvoiceAlreadyIssuedForBillingPeriod");

        // Preflight currently ALWAYS contains policy and ledger blockers.
        // A clean identity/scope result does not override those blockers
        // or allow a new Issue/PaymentAllocation write path.
        if (preflight.Blockers.Count != 0)
            result.Blockers.Add("FinancialIssuancePrerequisitesIncomplete");

        return result;
    }

    private static bool IsSha256(string hash)
        => hash.Length == 64 &&
           hash.All(c => c is >= '0' and <= '9' or
                            >= 'a' and <= 'f' or
                            >= 'A' and <= 'F');
}
