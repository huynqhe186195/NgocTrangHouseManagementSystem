using System;
using System.Collections.Generic;

namespace BuildingManagement.Utilities;

/// <summary>
/// Read-only preview of posted meter units for a room and billing period.
/// Not a final invoice and not proof that the period is complete.
/// </summary>
public sealed class MonthlyUsagePreviewDto
{
    public Guid RoomId { get; set; }
    public UtilityType UtilityType { get; set; }
    public int BillingYear { get; set; }
    public int BillingMonth { get; set; }
    public decimal RecordedUsage { get; set; }

    /// <summary>
    /// Conservative warnings about missing reads, month boundaries, or
    /// inconsistent meter handovers. These do NOT replace final bill approval.
    /// </summary>
    public List<string> CoverageIssues { get; set; } = new();

    public bool IsSafeForProvisionalPricing => CoverageIssues.Count == 0;

    public List<MeterUsageSegmentDto> Segments { get; set; } = new();
}

public sealed class MeterUsageSegmentDto
{
    public Guid SourceId { get; set; }
    public Guid UtilityMeterId { get; set; }

    /// <summary>
    /// "MonthlyReading" or "ReplacementClosing".
    /// </summary>
    public string SourceType { get; set; } = string.Empty;

    public DateTime RecordedDate { get; set; }
    public decimal PreviousReading { get; set; }
    public decimal CurrentReading { get; set; }
    public decimal Usage { get; set; }
}
