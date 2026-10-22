using System;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.MoveOuts;

public class MoveOutRequest : FullAuditedAggregateRoot<Guid>
{
    public Guid ContractId { get; private set; }
    public Guid RoomId { get; private set; }
    public DateOnly RequestedMoveOutDate { get; private set; }
    public DateOnly? ApprovedMoveOutDate { get; private set; }
    public string? Reason { get; private set; }
    public MoveOutRequestStatus Status { get; private set; }
    public string? ReviewNotes { get; private set; }
    public DateTime? ReviewedAt { get; private set; }
    public DateTime? CancelledAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    protected MoveOutRequest() { }

    public MoveOutRequest(
        Guid id, Guid contractId, Guid roomId,
        DateOnly requestedMoveOutDate, string? reason) : base(id)
    {
        if (contractId == Guid.Empty) throw new ArgumentException("ContractId required", nameof(contractId));
        if (roomId == Guid.Empty) throw new ArgumentException("RoomId required", nameof(roomId));
        ContractId = contractId;
        RoomId = roomId;
        RequestedMoveOutDate = requestedMoveOutDate;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        Status = MoveOutRequestStatus.Pending;
    }

    public void Approve(DateTime reviewedAt, string? notes)
    {
        if (Status != MoveOutRequestStatus.Pending)
            throw new InvalidOperationException("Only pending move-out requests can be approved.");
        Status = MoveOutRequestStatus.Approved;
        ApprovedMoveOutDate = RequestedMoveOutDate;
        ReviewedAt = reviewedAt;
        ReviewNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    }

    public void Reject(DateTime reviewedAt, string reason)
    {
        if (Status != MoveOutRequestStatus.Pending)
            throw new InvalidOperationException("Only pending move-out requests can be rejected.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Rejection reason is required", nameof(reason));
        Status = MoveOutRequestStatus.Rejected;
        ReviewedAt = reviewedAt;
        ReviewNotes = reason.Trim();
    }

    public void Cancel(DateTime cancelledAt)
    {
        if (Status != MoveOutRequestStatus.Pending)
            throw new InvalidOperationException("Only pending move-out requests can be cancelled.");
        Status = MoveOutRequestStatus.Cancelled;
        CancelledAt = cancelledAt;
    }

    // Only the later CompleteMoveOut orchestration may call this method.
    public void Complete(DateTime completedAt)
    {
        if (Status != MoveOutRequestStatus.Approved)
            throw new InvalidOperationException("Only approved move-out requests can be completed.");
        Status = MoveOutRequestStatus.Completed;
        CompletedAt = completedAt;
    }
}
