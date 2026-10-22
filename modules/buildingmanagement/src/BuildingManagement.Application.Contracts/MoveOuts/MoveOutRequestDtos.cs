using System;
using System.ComponentModel.DataAnnotations;

namespace BuildingManagement.MoveOuts;

public sealed class CreateMoveOutRequestDto
{
    public Guid ContractId { get; set; }
    // JSON date, e.g. "2026-11-30", no timezone component.
    public DateOnly RequestedMoveOutDate { get; set; }

    [StringLength(1024)]
    public string? Reason { get; set; }
}

public sealed class ApproveMoveOutRequestDto
{
    [StringLength(1024)]
    public string? ReviewNotes { get; set; }
}

public sealed class RejectMoveOutRequestDto
{
    [Required]
    [StringLength(1024)]
    public string Reason { get; set; } = string.Empty;
}

public sealed class MoveOutRequestDto
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public Guid RoomId { get; set; }
    public DateOnly RequestedMoveOutDate { get; set; }
    public DateOnly? ApprovedMoveOutDate { get; set; }
    public string? Reason { get; set; }
    public MoveOutRequestStatus Status { get; set; }
    public string? ReviewNotes { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public sealed class MoveOutChecklistTaskDto
{
    public Guid Id { get; set; }
    public Guid MoveOutRequestId { get; set; }
    public MoveOutChecklistTaskType Type { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime? CompletedAt { get; set; }
}
