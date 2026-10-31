using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Rooms;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// Atomic transfer audit: the current supported business lane is new linked
/// contract, whole old calendar month, same rent/deposit, all existing tenants
/// and an exclusive completed carry-forward. Every alternative OI-07 lane
/// requires its own explicitly approved financial/execution workflow.
/// This is not a receipt, invoice, refund or an external bank confirmation.
/// </summary>
public sealed class RoomChangeCompletionRecord : Entity<Guid>
{
    public Guid RequestId { get; private set; }
    public Guid SourceContractId { get; private set; }
    public Guid TargetContractId { get; private set; }
    public Guid SourceRoomId { get; private set; }
    public Guid TargetRoomId { get; private set; }
    public Guid BuildingId { get; private set; }
    public Guid PolicyId { get; private set; }
    public Guid PolicyReviewId { get; private set; }
    public Guid RosterSnapshotId { get; private set; }
    public Guid HandoverEvidenceId { get; private set; }
    public Guid CarryForwardSettlementId { get; private set; }
    public decimal CarriedDepositAmount { get; private set; }
    public RoomStatus SourceRoomDisposition { get; private set; }
    public string SourceDispositionEvidenceReference { get; private set; } = string.Empty;
    public string CompletionReference { get; private set; } = string.Empty;
    public Guid CompletedByUserId { get; private set; }
    public DateTime CompletedAtUtc { get; private set; }
    public string EvidenceSha256 { get; private set; } = string.Empty;

    private RoomChangeCompletionRecord() { }
    private RoomChangeCompletionRecord(Guid id) : base(id) { }

    public static RoomChangeCompletionRecord Complete(
        Guid id, RoomChangeRequest request, Contract source, Contract target,
        RoomChangeBusinessPolicyRecord policy,
        RoomChangeBusinessPolicyReviewRecord review,
        RoomChangeTenantRosterSnapshotRecord roster,
        RoomChangeHandoverEvidenceRecord handover,
        DepositSettlement carryForward, RoomStatus sourceDisposition,
        string? dispositionEvidenceReference, string? completionReference,
        Guid actor, DateTime atUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(handover);
        ArgumentNullException.ThrowIfNull(carryForward);
        policy.Verify(request, roster);
        review.Verify(policy, request, roster);
        if (id == Guid.Empty || actor == Guid.Empty ||
            atUtc.Kind != DateTimeKind.Utc || atUtc < review.ReviewedAtUtc ||
            string.IsNullOrWhiteSpace(dispositionEvidenceReference) ||
            dispositionEvidenceReference.Trim().Length is < 12 or > 500 ||
            string.IsNullOrWhiteSpace(completionReference) ||
            completionReference.Trim().Length is < 12 or > 500 ||
            atUtc < handover.AttestedAtUtc ||
            request.Status is not (RoomChangeRequestStatus.Pending or
                RoomChangeRequestStatus.PolicyHold) ||
            !review.Accepted ||
            policy.ContractTreatment != RoomChangeContractTreatment.NewContract ||
            policy.PricingTreatment != RoomChangePricingTreatment.FullOldMonthNewNextMonth ||
            policy.DepositTreatment != RoomChangeDepositTreatment.CarryAllWithNoDifference ||
            policy.TenantTreatment != RoomChangeTenantTreatment.AllCurrentTenants ||
            request.RequestedDate.Day != DateTime.DaysInMonth(
                request.RequestedDate.Year, request.RequestedDate.Month) ||
            request.RequestedDate >= DateOnly.MaxValue ||
            request.RequestedDate > DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTimeFromUtc(atUtc,
                    TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"))) ||
            source.Id != request.SourceContractId ||
            source.RoomId != request.SourceRoomId ||
            source.Status != ContractStatus.Ended ||
            source.EndReason != ContractEndReason.RoomChanged ||
            source.ActualEndedAt is null ||
            target.Id == source.Id || target.Id == Guid.Empty ||
            target.RoomId != request.TargetRoomId ||
            target.RenewedFromContractId != source.Id ||
            target.Status != ContractStatus.Active ||
            DateOnly.FromDateTime(target.StartDate) != request.RequestedDate.AddDays(1) ||
            source.MonthlyRent <= 0m || source.MonthlyRent != target.MonthlyRent ||
            source.DepositAmount <= 0m || source.DepositAmount != target.DepositAmount ||
            handover.RequestId != request.Id ||
            handover.SourceContractId != source.Id ||
            handover.SourceRoomId != source.RoomId ||
            handover.BuildingId != request.BuildingId ||
            handover.HandoverDate != request.RequestedDate ||
            handover.Id == Guid.Empty ||
            carryForward.Type != DepositSettlementType.RoomChangeCarryForward ||
            carryForward.Status != DepositSettlementStatus.Completed ||
            carryForward.SourceContractId != source.Id ||
            carryForward.TargetContractId != target.Id ||
            carryForward.Amount != source.DepositAmount ||
            carryForward.CompletedAt is null ||
            sourceDisposition is not (RoomStatus.Available or
                RoomStatus.Reserved or RoomStatus.Maintenance))
            throw new InvalidOperationException(
                "Unsupported OI-07 lane or incomplete atomic, settled transfer.");

        var result = new RoomChangeCompletionRecord(id)
        {
            RequestId = request.Id, SourceContractId = source.Id,
            TargetContractId = target.Id, SourceRoomId = source.RoomId,
            TargetRoomId = target.RoomId, BuildingId = request.BuildingId,
            PolicyId = policy.Id, PolicyReviewId = review.Id,
            RosterSnapshotId = roster.Id, HandoverEvidenceId = handover.Id,
            CarryForwardSettlementId = carryForward.Id,
            CarriedDepositAmount = carryForward.Amount,
            SourceRoomDisposition = sourceDisposition,
            SourceDispositionEvidenceReference = dispositionEvidenceReference!.Trim(),
            CompletionReference = completionReference!.Trim(),
            CompletedByUserId = actor,
            CompletedAtUtc = atUtc.AddTicks(-(atUtc.Ticks % 10))
        };
        result.EvidenceSha256 = result.Hash();
        return result;
    }

    public void VerifyFingerprint()
    {
        if (Id == Guid.Empty || RequestId == Guid.Empty ||
            SourceContractId == Guid.Empty || TargetContractId == Guid.Empty ||
            SourceContractId == TargetContractId ||
            SourceRoomId == Guid.Empty || TargetRoomId == Guid.Empty ||
            SourceRoomId == TargetRoomId || BuildingId == Guid.Empty ||
            PolicyId == Guid.Empty || PolicyReviewId == Guid.Empty ||
            RosterSnapshotId == Guid.Empty || HandoverEvidenceId == Guid.Empty ||
            CarryForwardSettlementId == Guid.Empty ||
            SourceDispositionEvidenceReference.Trim().Length is < 12 or > 500 ||
            CompletionReference.Trim().Length is < 12 or > 500 ||
            CompletedByUserId == Guid.Empty ||
            CompletedAtUtc.Kind != DateTimeKind.Utc ||
            CarriedDepositAmount <= 0m ||
            SourceRoomDisposition is not
                (RoomStatus.Available or RoomStatus.Reserved or RoomStatus.Maintenance) ||
            EvidenceSha256 != Hash())
            throw new InvalidDataException("Completed transfer evidence was rewritten.");
    }

    private string Hash()
    {
        var value = string.Join("\n", new[]
        {
            Id.ToString("D"), RequestId.ToString("D"),
            SourceContractId.ToString("D"), TargetContractId.ToString("D"),
            SourceRoomId.ToString("D"), TargetRoomId.ToString("D"),
            BuildingId.ToString("D"), PolicyId.ToString("D"),
            PolicyReviewId.ToString("D"), RosterSnapshotId.ToString("D"),
            HandoverEvidenceId.ToString("D"),
            CarryForwardSettlementId.ToString("D"),
            CarriedDepositAmount.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
            ((int)SourceRoomDisposition).ToString(System.Globalization.CultureInfo.InvariantCulture),
            SourceDispositionEvidenceReference, CompletionReference,
            CompletedByUserId.ToString("D"), CompletedAtUtc.ToString("O")
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }
}
