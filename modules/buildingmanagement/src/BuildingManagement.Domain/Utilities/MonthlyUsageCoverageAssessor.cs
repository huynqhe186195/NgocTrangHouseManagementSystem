using System;
using System.Collections.Generic;
using System.Linq;

namespace BuildingManagement.Utilities;

/// <summary>
/// Conservative warnings for assigning recorded meter deltas to a month.
/// A clean result is only permission for a PROVISIONAL price preview; it
/// never constitutes final-bill approval or evidence of a contractual debt.
/// </summary>
public static class MonthlyUsageCoverageAssessor
{
    public static IReadOnlyList<string> Assess(
        Guid roomId,
        UtilityType utilityType,
        int billingYear,
        int billingMonth,
        IEnumerable<UtilityMeter> meters,
        IEnumerable<MeterReading> readings,
        IEnumerable<MeterReplacement> replacements)
    {
        ArgumentNullException.ThrowIfNull(meters);
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(replacements);
        var monthStart = new DateTime(billingYear, billingMonth, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var previousMonth = monthStart.AddMonths(-1);

        var relevantMeters = meters
            .Where(x => x.RoomId == roomId && x.UtilityType == utilityType)
            .ToList();
        var meterIds = relevantMeters.Select(x => x.Id).ToHashSet();
        var history = readings.Where(x => meterIds.Contains(x.UtilityMeterId))
            .ToList();
        var thisMonthReadings = history
            .Where(x => x.BillingYear == billingYear &&
                        x.BillingMonth == billingMonth)
            .ToList();
        var thisMonthReplacements = replacements
            .Where(x => x.RoomId == roomId && x.UtilityType == utilityType &&
                        x.ReplacementDate.Date >= monthStart &&
                        x.ReplacementDate.Date <= monthEnd)
            .ToList();

        var issues = new HashSet<string>(StringComparer.Ordinal);
        if (relevantMeters.Count == 0)
            issues.Add("NoMeterForRoomAndUtility");

        if (history.Any(x => x.IsDeleted))
            issues.Add("SoftDeletedReadingHistoryRequiresReview");

        // Do not quote a zero bill when the current active meter has no
        // reading for this month (including a newly installed meter).
        foreach (var meter in relevantMeters
                     .Where(x => x.IsActive && x.InstalledDate.Date <= monthEnd))
        {
            if (!thisMonthReadings.Any(x => x.UtilityMeterId == meter.Id))
                issues.Add("ActiveMeterMissingMonthlyReading");
        }

        // Billing month and physical reading date are distinct concepts.
        // The cutoff policy is not yet approved: surface this mismatch
        // for human review instead of silently charging the wrong month.
        if (thisMonthReadings.Any(x => x.ReadingDate.Date < monthStart ||
                                      x.ReadingDate.Date > monthEnd))
            issues.Add("ReadingDateOutsideBillingMonth");

        // When a meter was already installed before this month, a single
        // reading may span several unbilled months. Without last month's
        // closing anchor it is unsafe to attribute the whole delta here.
        foreach (var meter in relevantMeters)
        {
            var isReadThisMonth = thisMonthReadings
                .Any(x => x.UtilityMeterId == meter.Id);
            var isClosedThisMonth = thisMonthReplacements
                .Any(x => x.OldMeterId == meter.Id);

            if (!isReadThisMonth && !isClosedThisMonth)
                continue;
            if (meter.InstalledDate.Date >= monthStart)
                continue;

            var hasPreviousMonthReading = history.Any(x =>
                x.UtilityMeterId == meter.Id &&
                x.BillingYear == previousMonth.Year &&
                x.BillingMonth == previousMonth.Month && !x.IsDeleted);

            // A reading taken this month is a valid anchor for the closing
            // snapshot, but it still needs its own previous-month basis.
            if (!hasPreviousMonthReading)
                issues.Add("MissingPreviousMonthReading");
        }

        // The previous counter value in each handover must correspond to
        // the latest monthly reading of that meter, if one was posted.
        // This catches inconsistent imports or tampering without silently
        // double counting a portion of consumption.
        foreach (var replacement in thisMonthReplacements)
        {
            var latest = history.Where(x => x.UtilityMeterId == replacement.OldMeterId &&
                                            !x.IsDeleted &&
                                            x.ReadingDate.Date <= replacement.ReplacementDate.Date)
                .OrderByDescending(x => x.ReadingDate)
                .FirstOrDefault();

            var meter = relevantMeters.FirstOrDefault(x => x.Id == replacement.OldMeterId);
            var expected = latest?.CurrentReading ?? meter?.InitialReading;
            if (expected is null || expected.Value != replacement.OldPreviousReading)
                issues.Add("ReplacementClosingBasisMismatch");

            var next = relevantMeters.FirstOrDefault(x => x.Id == replacement.NewMeterId);
            if (next is null ||
                next.InitialReading != replacement.NewInitialReading ||
                next.InstalledDate.Date != replacement.ReplacementDate.Date)
                issues.Add("ReplacementOpeningBasisMismatch");
        }

        return issues.OrderBy(x => x, StringComparer.Ordinal).ToList().AsReadOnly();
    }
}
