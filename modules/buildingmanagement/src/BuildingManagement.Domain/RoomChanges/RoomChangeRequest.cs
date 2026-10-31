using System;
using BuildingManagement.Contracts;
using BuildingManagement.Rooms;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.RoomChanges;

/// <summary>
/// Phase 2H operator's request for review. NEVER changes occupancy,
/// contract, payment, meter, invoice or deposit by itself.
/// </summary>
public sealed class RoomChangeRequest : Entity<Guid>
{
    public Guid SourceContractId { get; private set; }
    public Guid SourceRoomId { get; private set; }
    public Guid TargetRoomId { get; private set; }
    public Guid BuildingId { get; private set; }
    public DateOnly RequestedDate { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public RoomChangeRequestStatus Status { get; private set; }
    public Guid RequestedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public Guid? ReviewedByUserId { get; private set; }
    public DateTime? ReviewedAtUtc { get; private set; }
    public string? ReviewNotes { get; private set; }

    private RoomChangeRequest() { }
    private RoomChangeRequest(Guid id) : base(id) { }

    public static RoomChangeRequest Submit(
        Guid id, Guid contractId, Guid sourceRoomId, Guid targetRoomId,
        Guid buildingId, DateOnly requestedDate, string? reason,
        Guid actorId, DateTime createdAtUtc,
        ContractStatus sourceContractStatus,
        RoomStatus sourceRoomStatus, RoomStatus targetRoomStatus)
    {
        if (id == Guid.Empty || contractId == Guid.Empty ||
            sourceRoomId == Guid.Empty || targetRoomId == Guid.Empty ||
            buildingId == Guid.Empty || actorId == Guid.Empty ||
            sourceRoomId == targetRoomId ||
            requestedDate == default || createdAtUtc.Kind != DateTimeKind.Utc ||
            sourceContractStatus != ContractStatus.Active ||
            sourceRoomStatus != RoomStatus.Occupied ||
            targetRoomStatus != RoomStatus.Available ||
            string.IsNullOrWhiteSpace(reason) ||
            reason.Trim().Length is < 8 or > 500)
            throw new InvalidOperationException("Room-change intent must be scoped, active and documented.");

        return new RoomChangeRequest(id)
        {
            SourceContractId = contractId,
            SourceRoomId = sourceRoomId,
            TargetRoomId = targetRoomId,
            BuildingId = buildingId,
            RequestedDate = requestedDate,
            Reason = reason.Trim(),
            Status = RoomChangeRequestStatus.Pending,
            RequestedByUserId = actorId,
            CreatedAtUtc = createdAtUtc.AddTicks(-(createdAtUtc.Ticks % 10))
        };
    }

    public void HoldForPolicy(Guid reviewerId, DateTime reviewedAtUtc, string? note)
    {
        RequireReview(reviewerId, reviewedAtUtc, note);
        if (Status != RoomChangeRequestStatus.Pending)
            throw new InvalidOperationException("Only pending requests may be placed on hold.");
        Status = RoomChangeRequestStatus.PolicyHold;
        SetReview(reviewerId, reviewedAtUtc, note!);
    }

    public void Reject(Guid reviewerId, DateTime reviewedAtUtc, string? reason)
    {
        RequireReview(reviewerId, reviewedAtUtc, reason);
        if (Status is not (RoomChangeRequestStatus.Pending or
            RoomChangeRequestStatus.PolicyHold))
            throw new InvalidOperationException("Only open room-change requests may be rejected.");
        Status = RoomChangeRequestStatus.Rejected;
        SetReview(reviewerId, reviewedAtUtc, reason!);
    }

    public void Cancel(Guid actorId, DateTime cancelledAtUtc)
    {
        if (actorId == Guid.Empty || cancelledAtUtc.Kind != DateTimeKind.Utc ||
            cancelledAtUtc < CreatedAtUtc ||
            Status is not (RoomChangeRequestStatus.Pending or
                RoomChangeRequestStatus.PolicyHold))
            throw new InvalidOperationException("Only open requests can be cancelled.");
        Status = RoomChangeRequestStatus.Cancelled;
        SetReview(actorId, cancelledAtUtc, "Cancelled before transfer authorization.");
    }

    /// <summary>Only the scoped atomic completion writer may call this.</summary>
    public void Complete(Guid actor, DateTime atUtc, string reference)
    {
        if (actor == Guid.Empty || atUtc.Kind != DateTimeKind.Utc ||
            atUtc < CreatedAtUtc ||
            Status is not (RoomChangeRequestStatus.Pending or
                RoomChangeRequestStatus.PolicyHold) ||
            string.IsNullOrWhiteSpace(reference) ||
            reference.Trim().Length is < 12 or > 500)
            throw new InvalidOperationException("Completed room change requires immutable audit reference.");
        Status = RoomChangeRequestStatus.Completed;
        SetReview(actor, atUtc, reference);
    }

    private void RequireReview(Guid reviewerId, DateTime atUtc, string? reason)
    {
        if (reviewerId == Guid.Empty || atUtc.Kind != DateTimeKind.Utc ||
            atUtc < CreatedAtUtc ||
            string.IsNullOrWhiteSpace(reason) ||
            reason.Trim().Length is < 8 or > 500)
            throw new InvalidOperationException("A documented, authenticated review is required.");
    }

    private void SetReview(Guid actor, DateTime atUtc, string reason)
    {
        ReviewedByUserId = actor;
        ReviewedAtUtc = atUtc.AddTicks(-(atUtc.Ticks % 10));
        ReviewNotes = reason.Trim();
    }
}
