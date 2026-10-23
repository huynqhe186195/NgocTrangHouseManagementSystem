using System;
using System.Collections.Generic;
using System.Linq;
using BuildingManagement.Utilities;

namespace BuildingManagement.Invoices;

/// <summary>
/// Immutable, in-memory candidate snapshot for Phase 2G.3A.1.
/// It is NOT an issued invoice, a payment claim, or a persisted entity.
/// Issuance, tenant allocation, rounding and adjustment rules are pending.
/// </summary>
public sealed class InvoiceDraftSnapshot
{
    public Guid ContractId { get; }
    public Guid RoomId { get; }
    public int BillingYear { get; }
    public int BillingMonth { get; }
    public IReadOnlyList<InvoiceDraftUtilityLineSnapshot> UtilityLines { get; }
    public decimal? ProvisionalTotal { get; }

    // No authorization path from a read-only candidate to a real invoice.
    public bool CanIssue => false;
    public bool IsPersisted => false;

    private InvoiceDraftSnapshot(
        Guid contractId,
        Guid roomId,
        int billingYear,
        int billingMonth,
        IReadOnlyList<InvoiceDraftUtilityLineSnapshot> lines)
    {
        ContractId = contractId;
        RoomId = roomId;
        BillingYear = billingYear;
        BillingMonth = billingMonth;
        UtilityLines = lines;

        // Do not turn missing price/coverage into a zero-amount charge.
        ProvisionalTotal = lines.Count > 0 && lines.All(x => x.ProvisionalAmount.HasValue)
            ? lines.Sum(x => x.ProvisionalAmount!.Value)
            : null;
    }

    public static InvoiceDraftSnapshot Capture(
        Guid contractId,
        Guid roomId,
        int billingYear,
        int billingMonth,
        IEnumerable<InvoiceDraftUtilityLineSnapshot> utilityLines)
    {
        if (contractId == Guid.Empty || roomId == Guid.Empty)
            throw new ArgumentException("Contract and room must be specified.");
        if (billingYear is < 1 or > 9999)
            throw new ArgumentOutOfRangeException(nameof(billingYear));
        if (billingMonth is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(billingMonth));
        ArgumentNullException.ThrowIfNull(utilityLines);

        var lines = utilityLines.ToArray();
        if (lines.Length == 0 || lines.Any(x => x is null))
            throw new ArgumentException("At least one valid utility line is required.",
                nameof(utilityLines));
        if (lines.Select(x => x.UtilityType).Distinct().Count() != lines.Length)
            throw new ArgumentException("Duplicate utility type in draft snapshot.",
                nameof(utilityLines));

        // The same posted reading or handover snapshot cannot be attributed
        // to two different utility charges in the same draft.
        var sourceKeys = lines.SelectMany(x => x.MeterSegments)
            .Select(x => (x.SourceType, x.SourceId)).ToArray();
        if (sourceKeys.Distinct().Count() != sourceKeys.Length)
            throw new ArgumentException("Meter source counted in multiple utility lines.",
                nameof(utilityLines));

        // Make defensive copies of all source identifiers and counter ranges.
        // Future edits to DTOs or list objects cannot mutate this snapshot.
        var copies = lines.Select(x => x.Copy()).ToList().AsReadOnly();
        return new InvoiceDraftSnapshot(
            contractId, roomId, billingYear, billingMonth, copies);
    }
}

public sealed record InvoiceDraftMeterSegmentSnapshot(
    Guid SourceId,
    Guid UtilityMeterId,
    MeterUsageSourceType SourceType,
    DateTime RecordedDate,
    decimal PreviousReading,
    decimal CurrentReading)
{
    public decimal Quantity => CurrentReading - PreviousReading;
}

/// <summary>
/// One utility's RECORDED units, linked back to the physical reading/handover
/// sources. Rate values are copied by value, never linked for future pricing.
/// A missing provisional price deliberately remains null.
/// </summary>
public sealed class InvoiceDraftUtilityLineSnapshot
{
    public UtilityType UtilityType { get; }
    public Guid? UtilityRateId { get; }
    public string RateStatus { get; }
    public decimal Quantity { get; }
    public decimal? UnitPrice { get; }
    public decimal? ProvisionalAmount { get; }
    public IReadOnlyList<string> CoverageIssues { get; }
    public IReadOnlyList<InvoiceDraftMeterSegmentSnapshot> MeterSegments { get; }

    public InvoiceDraftUtilityLineSnapshot(
        UtilityType utilityType,
        Guid? utilityRateId,
        string rateStatus,
        decimal quantity,
        decimal? unitPrice,
        decimal? provisionalAmount,
        IEnumerable<InvoiceDraftMeterSegmentSnapshot> meterSegments,
        IEnumerable<string> coverageIssues)
    {
        if (!Enum.IsDefined(typeof(UtilityType), utilityType))
            throw new ArgumentOutOfRangeException(nameof(utilityType));
        if (string.IsNullOrWhiteSpace(rateStatus))
            throw new ArgumentException("Rate status is required.", nameof(rateStatus));
        if (quantity < 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        ArgumentNullException.ThrowIfNull(meterSegments);
        ArgumentNullException.ThrowIfNull(coverageIssues);

        var segments = meterSegments.ToArray();
        var issues = coverageIssues.ToArray();
        if (segments.Any(x => x is null ||
                              x.SourceId == Guid.Empty ||
                              x.UtilityMeterId == Guid.Empty ||
                              !Enum.IsDefined(typeof(MeterUsageSourceType), x.SourceType) ||
                              x.PreviousReading < 0 ||
                              x.CurrentReading < x.PreviousReading))
            throw new ArgumentException("Invalid meter segment snapshot.",
                nameof(meterSegments));
        if (segments.Select(x => (x.SourceType, x.SourceId)).Distinct().Count() !=
            segments.Length)
            throw new ArgumentException("Duplicate meter usage source.",
                nameof(meterSegments));
        if (segments.Sum(x => x.Quantity) != quantity)
            throw new ArgumentException("Snapshot quantity does not match segment totals.",
                nameof(quantity));
        if (issues.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Invalid coverage issue.", nameof(coverageIssues));

        bool fullyPriced = provisionalAmount.HasValue;
        if (fullyPriced)
        {
            if (issues.Length != 0 ||
                rateStatus != "SingleRateWholeMonth" ||
                utilityRateId.GetValueOrDefault() == Guid.Empty ||
                !unitPrice.HasValue || unitPrice.Value <= 0 ||
                provisionalAmount.Value != quantity * unitPrice.Value)
                throw new ArgumentException(
                    "Provisional amount requires one covering rate, clean coverage and exact source quantity.");
        }
        else if (unitPrice.HasValue && unitPrice.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(unitPrice));

        UtilityType = utilityType;
        UtilityRateId = utilityRateId;
        RateStatus = rateStatus;
        Quantity = quantity;
        UnitPrice = unitPrice;
        ProvisionalAmount = provisionalAmount;
        CoverageIssues = Array.AsReadOnly(issues);
        MeterSegments = Array.AsReadOnly(segments);
    }

    internal InvoiceDraftUtilityLineSnapshot Copy() => new(
        UtilityType, UtilityRateId, RateStatus, Quantity, UnitPrice,
        ProvisionalAmount, MeterSegments, CoverageIssues);
}
