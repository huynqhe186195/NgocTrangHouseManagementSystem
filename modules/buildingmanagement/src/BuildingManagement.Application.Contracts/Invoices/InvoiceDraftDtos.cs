using System;
using System.Collections.Generic;
using BuildingManagement.Utilities;

namespace BuildingManagement.Invoices;

/// <summary>
/// Only contract and billing period are client-controlled.
/// Unit prices, amounts, meter IDs and source IDs are NEVER accepted here.
/// </summary>
public sealed class CreateInvoiceDraftDto
{
    public Guid ContractId { get; set; }
    public int BillingYear { get; set; }
    public int BillingMonth { get; set; }
}

/// <summary>
/// Stored utility-only draft. It is not an issued bill or amount due.
/// </summary>
public sealed class InvoiceDraftDto
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public Guid RoomId { get; set; }
    public int BillingYear { get; set; }
    public int BillingMonth { get; set; }
    public InvoiceStatus Status { get; set; }
    public decimal? ProvisionalUtilityTotal { get; set; }
    public List<InvoiceDraftItemDto> Items { get; set; } = new();
    public bool IsIssued => false;
    public bool HasRecognizedDebt => false;
}

public sealed class InvoiceDraftItemDto
{
    public Guid Id { get; set; }
    public UtilityType UtilityType { get; set; }
    public Guid? UtilityRateId { get; set; }
    public decimal Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? ProvisionalAmount { get; set; }
    public string RateStatus { get; set; } = string.Empty;
    public List<string> CoverageIssues { get; set; } = new();
    public List<InvoiceDraftSourceDto> Sources { get; set; } = new();
}

public sealed class InvoiceDraftSourceDto
{
    public Guid SourceId { get; set; }
    public Guid UtilityMeterId { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public DateTime RecordedDate { get; set; }
    public decimal PreviousReading { get; set; }
    public decimal CurrentReading { get; set; }
    public decimal Quantity { get; set; }
}
