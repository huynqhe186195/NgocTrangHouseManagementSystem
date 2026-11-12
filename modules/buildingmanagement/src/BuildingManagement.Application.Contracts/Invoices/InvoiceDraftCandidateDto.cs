using System;
using System.Collections.Generic;
using BuildingManagement.Utilities;

namespace BuildingManagement.Invoices;

/// <summary>
/// Contract-scoped, read-only candidate. NOT a persisted or issued invoice.
/// Monetary values are provisional and null when not safely attributable.
/// </summary>
public sealed class InvoiceDraftCandidateDto
{
    public Guid ContractId { get; set; }
    public Guid RoomId { get; set; }
    public int BillingYear { get; set; }
    public int BillingMonth { get; set; }

    public List<string> Blockers { get; set; } = new();
    public List<InvoiceDraftUtilityLineCandidateDto> UtilityLines { get; set; } = new();
    public decimal? ProvisionalTotal { get; set; }

    public bool CanPersistDraft => false;
    public bool CanIssue => false;
}

public sealed class InvoiceDraftUtilityLineCandidateDto
{
    public UtilityType UtilityType { get; set; }
    public Guid? UtilityRateId { get; set; }
    public string RateStatus { get; set; } = string.Empty;
    public decimal RecordedQuantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? ProvisionalAmount { get; set; }
    public List<string> CoverageIssues { get; set; } = new();
    public List<MeterUsageSegmentDto> MeterSegments { get; set; } = new();
}
