using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BuildingManagement.Utilities;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.Invoices;

/// <summary>
/// Fixed draft line snapshot. Monetary amount is nullable until rate
/// coverage has been approved. The source IDs are preserved separately.
/// </summary>
public sealed class InvoiceItem : AuditedEntity<Guid>
{
    private readonly List<InvoiceItemMeterSource> _meterSources = new();

    public Guid InvoiceId { get; private set; }
    public UtilityType UtilityType { get; private set; }
    public Guid? UtilityRateId { get; private set; }
    public string RateStatus { get; private set; } = string.Empty;
    public decimal RecordedQuantity { get; private set; }
    public decimal? UnitPrice { get; private set; }
    public decimal? ProvisionalAmount { get; private set; }
    public string CoverageIssuesJson { get; private set; } = "[]";
    public IReadOnlyCollection<InvoiceItemMeterSource> MeterSources => _meterSources.AsReadOnly();

    private InvoiceItem() { }

    private InvoiceItem(Guid id, Guid invoiceId, InvoiceDraftUtilityLineSnapshot source)
        : base(id)
    {
        InvoiceId = invoiceId;
        UtilityType = source.UtilityType;
        UtilityRateId = source.UtilityRateId;
        RateStatus = source.RateStatus;
        RecordedQuantity = source.Quantity;
        UnitPrice = source.UnitPrice;
        ProvisionalAmount = source.ProvisionalAmount;
        CoverageIssuesJson = JsonSerializer.Serialize(source.CoverageIssues);
    }

    internal static InvoiceItem FromSnapshot(
        Guid id, Guid invoiceId, InvoiceDraftUtilityLineSnapshot source,
        Func<Guid> nextSourceId)
    {
        if (id == Guid.Empty || invoiceId == Guid.Empty)
            throw new ArgumentException("Invoice and line identifiers are required.");
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(nextSourceId);

        var line = new InvoiceItem(id, invoiceId, source);
        foreach (var segment in source.MeterSegments)
            line._meterSources.Add(new InvoiceItemMeterSource(
                nextSourceId(), id, segment));
        return line;
    }
}
