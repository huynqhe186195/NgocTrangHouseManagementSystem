using System;
using System.Collections.Generic;
using System.Linq;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// Fail-closed, server-fact-only transfer execution rehearsal. Never authorizes
/// writes: source final invoice, deposit and fresh occupancy facts are read by
/// an authenticated PostgreSQL adapter. This is not a replacement for the
/// still-missing same-UoW atomic transfer and financial settlement writer.
/// </summary>
public static class RoomChangeExecutionReadiness
{
    public static RoomChangeExecutionReadinessResult Assess(
        RoomChangeExecutionFacts? facts)
    {
        if (facts is null)
            return new(false, new[] { "ExecutionFactsMissing" });

        var blocked = new List<string>();
        if (!facts.CurrentRoomChangeReviewable)
            blocked.Add("CurrentRoomScopeOrRosterInvalid");
        if (!facts.IndependentPolicyAcceptedAndVerified)
            blocked.Add("ApprovedOI07PolicyMissingOrRejected");
        if (!facts.HandoverOriginalMetersAndSignedReferencesVerified)
            blocked.Add("SourcePhysicalHandoverNotVerified");
        if (facts.OtherOpenIntentForSameTarget)
            blocked.Add("CompetingTargetRoomChangeIntent");
        if (!facts.SourceFinalInvoiceOfficiallyPosted)
            blocked.Add("SourceFinalInvoiceNotOfficiallyPosted");
        if (!facts.AllSourceInvoicesOfficiallyPostedAndReviewed)
            blocked.Add("UnreconciledSourceReceivables");
        if (!facts.DepositOriginalPaymentsFullyProven)
            blocked.Add("ContractDepositProvenanceIncomplete");
        if (!facts.NoPendingOrOutgoingDepositDisposition)
            blocked.Add("DepositPendingOrAlreadyDisposed");
        if (!facts.TargetCommitmentsClear)
            blocked.Add("TargetRoomCommitmentUnresolved");

        if (!facts.ExplicitSupportedOI07PolicyLane)
            blocked.Add("UnsupportedOI07ExecutionPolicyLane");
        if (!facts.TransferMonthPhysicallyBilled)
            blocked.Add("TransferMonthOriginalMetersNotBilled");
        if (!facts.AtomicWriterRegistered)
            blocked.Add("AtomicRoomChangeExecutionNotImplemented");
        var distinct = blocked.Distinct().ToArray();
        return new(distinct.Length == 0, distinct);
    }
}

public sealed record RoomChangeExecutionFacts(
    bool CurrentRoomChangeReviewable,
    bool IndependentPolicyAcceptedAndVerified,
    bool HandoverOriginalMetersAndSignedReferencesVerified,
    bool OtherOpenIntentForSameTarget,
    bool SourceFinalInvoiceOfficiallyPosted,
    bool AllSourceInvoicesOfficiallyPostedAndReviewed,
    bool DepositOriginalPaymentsFullyProven,
    bool NoPendingOrOutgoingDepositDisposition,
    bool TargetCommitmentsClear,
    bool ExplicitSupportedOI07PolicyLane = false,
    bool TransferMonthPhysicallyBilled = false,
    bool AtomicWriterRegistered = false);

public sealed record RoomChangeExecutionReadinessResult(
    bool CanExecuteTransfer, IReadOnlyList<string> Blockers);
