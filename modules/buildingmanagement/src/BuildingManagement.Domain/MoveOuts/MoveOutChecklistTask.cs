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
