using System;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.Utilities;

/// <summary>
/// Immutable handover between two physical meters. Not a monthly bill/reading.
/// The old meter's unbilled closing segment is OldFinalReading - OldPreviousReading.
/// </summary>
public class MeterReplacement : AuditedAggregateRoot<Guid>
{
    public Guid OldMeterId { get; private set; }
    public Guid NewMeterId { get; private set; }
    public Guid RoomId { get; private set; }
    public UtilityType UtilityType { get; private set; }
    public DateTime ReplacementDate { get; private set; }
    public decimal OldPreviousReading { get; private set; }
    public decimal OldFinalReading { get; private set; }
    public decimal NewInitialReading { get; private set; }
    public string Reason { get; private set; } = default!;
    public string? Notes { get; private set; }

    protected MeterReplacement() { }

    public MeterReplacement(
        Guid id, Guid oldMeterId, Guid newMeterId, Guid roomId,
        UtilityType utilityType, DateTime replacementDate,
        decimal oldPreviousReading, decimal oldFinalReading,
        decimal newInitialReading, string reason, string? notes = null) : base(id)
    {
        if (oldMeterId == Guid.Empty || newMeterId == Guid.Empty ||
            oldMeterId == newMeterId || roomId == Guid.Empty)
            throw new ArgumentException("Replacement must have distinct existing meter identities and a room.");
        if (replacementDate == default || oldPreviousReading < 0 ||
            oldFinalReading < oldPreviousReading || newInitialReading < 0)
            throw new ArgumentException("Invalid replacement date or meter readings.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Replacement reason is required.", nameof(reason));
        OldMeterId = oldMeterId;
        NewMeterId = newMeterId;
        RoomId = roomId;
        UtilityType = utilityType;
        ReplacementDate = replacementDate.Date;
        OldPreviousReading = oldPreviousReading;
        OldFinalReading = oldFinalReading;
        NewInitialReading = newInitialReading;
        Reason = reason.Trim();
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    }

    public decimal ClosingUsage => OldFinalReading - OldPreviousReading;
}
