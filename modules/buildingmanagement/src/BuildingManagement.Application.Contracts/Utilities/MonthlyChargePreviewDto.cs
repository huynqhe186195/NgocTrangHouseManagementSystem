using System;

namespace BuildingManagement.Utilities;

/// <summary>
/// Preliminary monetary estimate based exclusively on RECORDED usage.
/// No guarantee of billing completeness, currency rounding or invoice issuance.
/// When tariff coverage is ambiguous, ProvisionalAmount is intentionally null.
/// </summary>
public sealed class MonthlyChargePreviewDto
{
    public Guid RoomId { get; set; }
    public Guid BuildingId { get; set; }
    public UtilityType UtilityType { get; set; }
    public int BillingYear { get; set; }
    public int BillingMonth { get; set; }

    public decimal RecordedUsage { get; set; }
    public Guid? UtilityRateId { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? ProvisionalAmount { get; set; }

    /// <summary>
    /// SingleRateWholeMonth, NoRate, IncompleteRateCoverage,
    /// MultipleRatesInMonth, or DeletedRateHistoryRequiresReview.
    /// </summary>
    public string RateStatus { get; set; } = string.Empty;

    public bool IsFinalInvoice => false;

    public MonthlyUsagePreviewDto UsagePreview { get; set; } = new();
}
