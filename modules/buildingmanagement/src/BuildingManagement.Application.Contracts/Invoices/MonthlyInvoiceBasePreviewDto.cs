using System;
using System.Collections.Generic;

namespace BuildingManagement.Invoices;

/// <summary>
/// Phase 2G.3 monthly RENT + electricity/water estimate only.
/// The specification does not approve arbitrary Internet, parking, trash,
/// damage or other service fees, so this is NEVER a complete invoice.
/// </summary>
public sealed class MonthlyInvoiceBasePreviewDto
{
    public Guid ContractId { get; set; }
    public Guid RoomId { get; set; }
    public int BillingYear { get; set; }
    public int BillingMonth { get; set; }
    public decimal KnownFullMonthRentAmount { get; set; }
    public decimal? ProvisionalElectricityAmount { get; set; }
    public decimal? ProvisionalWaterAmount { get; set; }
    public decimal? ProvisionalRentAndUtilitiesAmount { get; set; }

    // No missing service price may be interpreted as VND 0.
    public decimal? OtherServiceFeesAmount => null;
    public bool OtherServiceFeesReviewed => false;
    public bool CanIssue => false;
    public bool IsOfficialReceivable => false;
    public List<string> Blockers { get; set; } = new();
}
