using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// H4 immutable authorization-PROPOSAL evidence only. It records the Owner's
/// EXCLUSIVE choice (deferred deposit receivable, future-rent credit, or
/// manual refund) before any money is moved. The original source amounts
/// remain documentary assertions, not confirmed cash collection.
/// No invoice writer, transfer completion, bank payment or cash posting is
/// authorized by this record. Actual cash-backed settlement must be a
/// separate audited, authenticated, serializable transaction.
/// </summary>
public sealed record RoomChangeDepositDispositionIntentRecord(
    Guid Id,
    Guid RequestId,
    Guid SourceContractId,
    Guid TargetContractId,
    Guid BuildingId,
    DateOnly TransferDateVietnam,
    decimal ClaimedOriginalSourceHeldVnd,
    decimal AlreadyPostedApprovedDeductionsVnd,
    decimal ProposedCarryTargetVnd,
    decimal TargetRequiredDepositVnd,
    decimal ProposedUnpaidTopUpVnd,
    DateOnly? ProposedTopUpDueDateVietnam,
    decimal ProposedFutureRentCreditVnd,
    decimal ProposedManualRefundVnd,
    RoomChangeDepositSurplusChoice SurplusChoice,
    string OriginalCashLedgerReference,
    string TargetDepositPolicyReference,
    string? SignedDeductionReference,
    string OwnerSignedDispositionReference,
    Guid ProposedByOwnerId,
    DateTime ProposedAtUtc,
    string EvidenceSha256)
{
    public bool IsCashBackedPosting => false;
    public bool IsTopUpCollected => false;
    public bool IsTenantRefundPaid => false;
    public bool IsRentCreditApplied => false;
    public bool CanCompleteTransfer => false;

    public static RoomChangeDepositDispositionIntentRecord Capture(
        Guid id, RoomChangeRequest request,
        RoomChangeDepositDifferenceFacts originalFacts,
        RoomChangeDeferredDepositDispositionPlan proposed,
        Guid ownerId, DateTime atUtc,
        string? ownerSignedDispositionReference)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(originalFacts);
        ArgumentNullException.ThrowIfNull(proposed);
        if (id == Guid.Empty || ownerId == Guid.Empty ||
            request.Id != proposed.RequestId ||
            request.SourceContractId != proposed.SourceContractId ||
            request.BuildingId == Guid.Empty ||
            request.RequestedDate != proposed.TransferDateVietnam ||
            request.Status is not (RoomChangeRequestStatus.Pending or
                RoomChangeRequestStatus.PolicyHold) ||
            atUtc.Kind != DateTimeKind.Utc ||
            atUtc < request.CreatedAtUtc ||
            !ValidReference(ownerSignedDispositionReference) ||
            !ValidReference(originalFacts.SourceHeldCashLedgerReference) ||
            !ValidReference(originalFacts.TargetRequiredDepositPolicyReference))
            throw new InvalidOperationException(
                "H4 proposal requires an existing open same-building request, authentic Owner actor and original signed disposition reference.");

        // The original plan itself was just a preview. Reconstruct every
        // number from its original facts and reject an altered request DTO.
        var verified = RoomChangeDeferredDepositDispositionPlanner.Plan(
            request.Id, originalFacts, request.RequestedDate,
            proposed.SurplusChoice, proposed.OutstandingTopUpDueDateVietnam);
        if (verified != proposed)
            throw new InvalidOperationException(
                "H4 deposit choice does not match the original source accounting facts.");

        var stableUtc = atUtc.AddTicks(-(atUtc.Ticks % 10L));
        var record = new RoomChangeDepositDispositionIntentRecord(
            id, request.Id, proposed.SourceContractId,
            proposed.TargetContractId, request.BuildingId,
            request.RequestedDate, proposed.SourceCashBackedHeldVnd,
            proposed.AlreadyPostedApprovedDeductionsVnd,
            proposed.CarriedTargetDepositVnd,
            proposed.TargetRequiredDepositVnd,
            proposed.OutstandingTopUpReceivableVnd,
            proposed.OutstandingTopUpDueDateVietnam,
            proposed.NextMonthRentCreditLiabilityVnd,
            proposed.ManualRefundLiabilityVnd,
            proposed.SurplusChoice,
            originalFacts.SourceHeldCashLedgerReference!.Trim(),
            originalFacts.TargetRequiredDepositPolicyReference!.Trim(),
            originalFacts.OwnerApprovedDeductionReference?.Trim(),
            ownerSignedDispositionReference!.Trim(),
            ownerId, stableUtc, string.Empty);
        return record with { EvidenceSha256 = record.Fingerprint() };
    }

    public void VerifyFingerprint()
    {
        if (Id == Guid.Empty || RequestId == Guid.Empty ||
            BuildingId == Guid.Empty || SourceContractId == Guid.Empty ||
            TargetContractId == Guid.Empty ||
            SourceContractId == TargetContractId ||
            ProposedByOwnerId == Guid.Empty ||
            ProposedAtUtc.Kind != DateTimeKind.Utc ||
            ProposedAtUtc.Ticks % 10L != 0L ||
            !ValidReference(OwnerSignedDispositionReference) ||
            !ValidReference(OriginalCashLedgerReference) ||
            !ValidReference(TargetDepositPolicyReference) ||
            EvidenceSha256 != Fingerprint())
            throw new InvalidDataException(
                "Original H4 disposition intent evidence was modified.");

        // A SHA-256 checksum is not an Owner signature. Recompute the
        // exclusive top-up/credit/refund split from its frozen financial
        // facts, so a re-hashed but contradictory record also fails closed.
        try
        {
            var originalFacts = new RoomChangeDepositDifferenceFacts(
                SourceContractId, TargetContractId,
                ClaimedOriginalSourceHeldVnd,
                AlreadyPostedApprovedDeductionsVnd,
                TargetRequiredDepositVnd, OriginalCashLedgerReference,
                TargetDepositPolicyReference, SignedDeductionReference,
                HasUnresolvedDispute: false,
                HasEarlierRefundOrCarryForward: false);
            var canonical = RoomChangeDeferredDepositDispositionPlanner.Plan(
                RequestId, originalFacts, TransferDateVietnam,
                SurplusChoice, ProposedTopUpDueDateVietnam);
            if (ClaimedOriginalSourceHeldVnd != canonical.SourceCashBackedHeldVnd ||
                AlreadyPostedApprovedDeductionsVnd !=
                    canonical.AlreadyPostedApprovedDeductionsVnd ||
                ProposedCarryTargetVnd != canonical.CarriedTargetDepositVnd ||
                TargetRequiredDepositVnd != canonical.TargetRequiredDepositVnd ||
                ProposedUnpaidTopUpVnd != canonical.OutstandingTopUpReceivableVnd ||
                ProposedTopUpDueDateVietnam !=
                    canonical.OutstandingTopUpDueDateVietnam ||
                ProposedFutureRentCreditVnd !=
                    canonical.NextMonthRentCreditLiabilityVnd ||
                ProposedManualRefundVnd != canonical.ManualRefundLiabilityVnd)
                throw new InvalidDataException(
                    "H4 proposed deposit obligations violate the original conservation rules.");
        }
        catch (Exception e) when (e is InvalidOperationException or
            OverflowException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException(
                "H4 proposed deposit accounting cannot be reconstructed.", e);
        }
    }

    private static bool ValidReference(string? reference)
        => !string.IsNullOrWhiteSpace(reference) &&
           reference.Trim().Length is >= 12 and <= 500;

    private string Fingerprint()
    {
        var c=CultureInfo.InvariantCulture;
        var parts=new[] {
            Id.ToString("D"), RequestId.ToString("D"),
            SourceContractId.ToString("D"), TargetContractId.ToString("D"),
            BuildingId.ToString("D"), TransferDateVietnam.ToString("yyyy-MM-dd",c),
            ClaimedOriginalSourceHeldVnd.ToString("0",c),
            AlreadyPostedApprovedDeductionsVnd.ToString("0",c),
            ProposedCarryTargetVnd.ToString("0",c),
            TargetRequiredDepositVnd.ToString("0",c),
            ProposedUnpaidTopUpVnd.ToString("0",c),
            ProposedTopUpDueDateVietnam?.ToString("yyyy-MM-dd",c) ?? string.Empty,
            ProposedFutureRentCreditVnd.ToString("0",c),
            ProposedManualRefundVnd.ToString("0",c),
            ((int)SurplusChoice).ToString(c),
            OriginalCashLedgerReference,
            TargetDepositPolicyReference,
            SignedDeductionReference ?? string.Empty,
            OwnerSignedDispositionReference,
            ProposedByOwnerId.ToString("D"),
            ProposedAtUtc.ToString("O",c)
        };
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",parts))))
            .ToLowerInvariant();
    }
}
