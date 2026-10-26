using System;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.MoveOuts;

/// <summary>
/// Preparation record only: Phase 2G.1 creates tasks as incomplete.
/// Completion workflows will be integrated with meter, invoice and settlement later.
/// </summary>
public class MoveOutChecklistTask : AuditedAggregateRoot<Guid>
{
    public Guid MoveOutRequestId { get; private set; }
    public MoveOutChecklistTaskType Type { get; private set; }
    public bool IsCompleted { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    /// <summary>
    /// Marks ONLY the final-meter checklist complete, following a fresh
    /// service-level check of both physical readings under the contract lock.
    /// This is not an invoice, handover or CompleteMoveOut authorization.
    /// </summary>
    public void MarkFinalMeterReadingsVerified(DateTime verifiedAt)
    {
        if (Type != MoveOutChecklistTaskType.FinalMeterReading)
            throw new InvalidOperationException(
                "Only the final-meter checklist can be verified here.");
        if (IsCompleted)
            throw new InvalidOperationException(
                "Final-meter checklist was already verified.");
        if (verifiedAt == default)
            throw new ArgumentException(
                "A verification timestamp is required.", nameof(verifiedAt));
        IsCompleted = true;
        CompletedAt = verifiedAt;
    }

    protected MoveOutChecklistTask() { }

    public MoveOutChecklistTask(Guid id, Guid moveOutRequestId,
        MoveOutChecklistTaskType type) : base(id)
    {
        if (moveOutRequestId == Guid.Empty)
            throw new ArgumentException("MoveOutRequestId required", nameof(moveOutRequestId));
        MoveOutRequestId = moveOutRequestId;
        Type = type;
        IsCompleted = false;
    }
}
