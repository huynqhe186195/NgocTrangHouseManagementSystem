using System;
using BuildingManagement.Utilities;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.Invoices;

/// <summary>
/// Snapshot of each physical meter reading or immutable replacement closing.
/// SourceId is a polymorphic historical reference; source readings may be
/// soft-deleted, so the numerical evidence must survive independently.
/// </summary>
public sealed class InvoiceItemMeterSource : Entity<Guid>
{
    public Guid InvoiceItemId { get; private set; }
    public Guid SourceId { get; private set; }
    public Guid UtilityMeterId { get; private set; }
    public MeterUsageSourceType SourceType { get; private set; }
    public DateTime RecordedDate { get; private set; }
    public decimal PreviousReading { get; private set; }
    public decimal CurrentReading { get; private set; }
    public decimal Quantity => CurrentReading - PreviousReading;

    private InvoiceItemMeterSource() { }

    internal InvoiceItemMeterSource(
        Guid id, Guid itemId, InvoiceDraftMeterSegmentSnapshot source)
        : base(id)
    {
        if (id == Guid.Empty || itemId == Guid.Empty ||
            source.SourceId == Guid.Empty || source.UtilityMeterId == Guid.Empty)
            throw new ArgumentException("All draft line source identifiers are required.");
        InvoiceItemId = itemId;
        SourceId = source.SourceId;
        UtilityMeterId = source.UtilityMeterId;
        SourceType = source.SourceType;
        RecordedDate = source.RecordedDate;
        PreviousReading = source.PreviousReading;
        CurrentReading = source.CurrentReading;
    }
}
