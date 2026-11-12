using System;
using System.Collections.Generic;
using System.Linq;

namespace BuildingManagement.Utilities;

/// <summary>
/// Identifies a posted monthly reading or the immutable closing segment
/// captured when a physical meter is replaced.
/// </summary>
public enum MeterUsageSourceType
{
    MonthlyReading = 1,
    ReplacementClosing = 2
}

public sealed record MeterUsageSegment(
    Guid SourceId,
    Guid UtilityMeterId,
    MeterUsageSourceType SourceType,
    DateTime RecordedDate,
    decimal PreviousReading,
    decimal CurrentReading)
{
    public decimal Usage => CurrentReading - PreviousReading;
}

/// <summary>
/// A reconciliation of RECORDED units only. This is not a final invoice:
/// an active meter may still lack its period-end reading, and tariffs,
/// contract dates and invoice finalization are handled separately.
/// </summary>
public sealed class MonthlyMeterUsageBreakdown
{
    public Guid RoomId { get; }
    public UtilityType UtilityType { get; }
    public int BillingYear { get; }
    public int BillingMonth { get; }
    public IReadOnlyList<MeterUsageSegment> Segments { get; }
    public decimal RecordedUsage => Segments.Sum(x => x.Usage);

    internal MonthlyMeterUsageBreakdown(
        Guid roomId, UtilityType utilityType, int billingYear,
        int billingMonth, IReadOnlyList<MeterUsageSegment> segments)
    {
        RoomId = roomId;
        UtilityType = utilityType;
        BillingYear = billingYear;
        BillingMonth = billingMonth;
        Segments = segments;
    }
}

/// <summary>
/// Combines non-overlapping monthly readings and replacement closing
/// snapshots. Both contributions count: a monthly reading ends at its
/// current index, while the replacement snapshot starts at that index.
/// Uses replacement snapshots as stored; it never recalculates them from
/// mutable later data or silently drops soft-deleted reading history.
/// This pure calculator does not access a database or apply pricing policy.
/// </summary>
public static class MonthlyMeterUsageCalculator
{
    public static MonthlyMeterUsageBreakdown Calculate(
        Guid roomId,
        UtilityType utilityType,
        int billingYear,
        int billingMonth,
        IEnumerable<UtilityMeter> meters,
        IEnumerable<MeterReading> readings,
        IEnumerable<MeterReplacement> replacements)
    {
        if (roomId == Guid.Empty)
            throw new ArgumentException("A room is required.", nameof(roomId));
        if (!Enum.IsDefined(typeof(UtilityType), utilityType))
            throw new ArgumentOutOfRangeException(nameof(utilityType));
        if (billingYear is < 1 or > 9999)
            throw new ArgumentOutOfRangeException(nameof(billingYear));
        if (billingMonth is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(billingMonth));
        ArgumentNullException.ThrowIfNull(meters);
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(replacements);

        // Include inactive historical meters; those are precisely the meters
        // which supply the pre-replacement consumption.
        var meterIds = meters
            .Where(x => x.RoomId == roomId && x.UtilityType == utilityType)
            .Select(x => x.Id)
            .ToHashSet();

        var periodReadings = readings
            .Where(x => meterIds.Contains(x.UtilityMeterId) &&
                        x.BillingYear == billingYear &&
                        x.BillingMonth == billingMonth)
            .ToList();

        if (periodReadings.Any(x => x.IsDeleted))
            throw new InvalidOperationException(
                "Soft-deleted meter reading history requires review before usage reconciliation.");

        // Only one monthly reading per physical meter and billing period.
        if (periodReadings.GroupBy(x => x.UtilityMeterId).Any(x => x.Count() > 1))
            throw new InvalidOperationException(
                "Duplicate monthly readings for a physical meter and period.");

        var periodReplacements = replacements
            .Where(x => x.RoomId == roomId && x.UtilityType == utilityType &&
                        x.ReplacementDate.Year == billingYear &&
                        x.ReplacementDate.Month == billingMonth)
            .ToList();

        if (periodReplacements.Any(x =>
                !meterIds.Contains(x.OldMeterId) || !meterIds.Contains(x.NewMeterId)))
            throw new InvalidOperationException(
                "Replacement refers to a meter outside the requested room and utility type.");

        if (periodReplacements.GroupBy(x => x.OldMeterId).Any(x => x.Count() > 1))
            throw new InvalidOperationException(
                "Duplicate outgoing replacement for a physical meter.");

        var segments = new List<MeterUsageSegment>();

        foreach (var reading in periodReadings)
            segments.Add(new MeterUsageSegment(
                reading.Id, reading.UtilityMeterId,
                MeterUsageSourceType.MonthlyReading, reading.ReadingDate,
                reading.PreviousReading, reading.CurrentReading));

        foreach (var replacement in periodReplacements)
            segments.Add(new MeterUsageSegment(
                replacement.Id, replacement.OldMeterId,
                MeterUsageSourceType.ReplacementClosing, replacement.ReplacementDate,
                replacement.OldPreviousReading, replacement.OldFinalReading));

        if (segments.Any(x => x.PreviousReading < 0 ||
                              x.CurrentReading < x.PreviousReading))
            throw new InvalidOperationException(
                "Meter usage history contains an invalid reading range.");

        // A caller must not accidentally add the same snapshot twice.
        if (segments.GroupBy(x => new { x.SourceType, x.SourceId })
                    .Any(x => x.Count() > 1))
            throw new InvalidOperationException("Duplicate meter usage source.");

        return new MonthlyMeterUsageBreakdown(
            roomId, utilityType, billingYear, billingMonth,
            segments.OrderBy(x => x.RecordedDate)
                    .ThenBy(x => x.SourceType)
                    .ThenBy(x => x.SourceId)
                    .ToList().AsReadOnly());
    }
}
