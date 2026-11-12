using System;
using System.IO;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// Independent root review of ONE immutable, roster-linked OI-07 choice set.
/// Accepted is a signed business-policy decision for this request only,
/// NOT proof of money held, debt settled, final readings or handover.
/// </summary>
public sealed class RoomChangeBusinessPolicyReviewRecord : Entity<Guid>
{
    public Guid PolicyId { get; private set; }
    public Guid RequestId { get; private set; }
    public Guid BuildingId { get; private set; }
    public string ProposalSha256 { get; private set; } = string.Empty;
    public Guid ReviewedByUserId { get; private set; }
    public DateTime ReviewedAtUtc { get; private set; }
    public bool Accepted { get; private set; }
    public string SignedReviewReference { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;

    private RoomChangeBusinessPolicyReviewRecord() { }
    private RoomChangeBusinessPolicyReviewRecord(Guid id) : base(id) { }

    public static RoomChangeBusinessPolicyReviewRecord Review(
        Guid id, RoomChangeBusinessPolicyRecord policy,
        RoomChangeRequest request, RoomChangeTenantRosterSnapshotRecord roster,
        Guid reviewer, DateTime reviewedAtUtc, bool accepted,
        string? signedReviewReference, string? reason)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Verify(request, roster);
        var evidence = signedReviewReference?.Trim() ?? string.Empty;
        var why = reason?.Trim() ?? string.Empty;
        if (id == Guid.Empty || reviewer == Guid.Empty ||
            reviewer == policy.ProposedByUserId ||
            reviewedAtUtc.Kind != DateTimeKind.Utc ||
            reviewedAtUtc < policy.ProposedAtUtc ||
            request.Status is not (RoomChangeRequestStatus.Pending or
                RoomChangeRequestStatus.PolicyHold) ||
            evidence.Length is < 12 or > 500 ||
            why.Length is < 12 or > 500)
            throw new InvalidOperationException(
                "Independent root policy review and signed references are required.");

        return new RoomChangeBusinessPolicyReviewRecord(id)
        {
            PolicyId = policy.Id,
            RequestId = policy.RequestId,
            BuildingId = policy.BuildingId,
            ProposalSha256 = policy.EvidenceSha256,
            ReviewedByUserId = reviewer,
            ReviewedAtUtc = reviewedAtUtc.AddTicks(-(reviewedAtUtc.Ticks % 10)),
            Accepted = accepted,
            SignedReviewReference = evidence,
            Reason = why
        };
    }

    public void Verify(RoomChangeBusinessPolicyRecord policy,
        RoomChangeRequest request, RoomChangeTenantRosterSnapshotRecord roster)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Verify(request, roster);
        if (Id == Guid.Empty || PolicyId != policy.Id ||
            RequestId != policy.RequestId || BuildingId != policy.BuildingId ||
            ProposalSha256 != policy.EvidenceSha256 ||
            ReviewedByUserId == Guid.Empty ||
            ReviewedByUserId == policy.ProposedByUserId ||
            ReviewedAtUtc.Kind != DateTimeKind.Utc ||
            ReviewedAtUtc < policy.ProposedAtUtc ||
            SignedReviewReference.Length is < 12 or > 500 ||
            Reason.Length is < 12 or > 500)
            throw new InvalidDataException("Transfer policy review does not match proposal.");
    }

    public bool IsTransferExecuted => false;
}
