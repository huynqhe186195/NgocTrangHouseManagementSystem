using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Rooms;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Uow;

namespace BuildingManagement.Utilities;

/// <summary>
/// Read-only preview from persisted monthly readings and immutable meter
/// replacement snapshots. No rate selection, invoice creation or mutation.
/// </summary>
public class UtilityUsagePreviewAppService
    : ApplicationService, IUtilityUsagePreviewAppService
{
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IRepository<UtilityMeter, Guid> _meters;
    private readonly IRepository<MeterReading, Guid> _readings;
    private readonly IRepository<MeterReplacement, Guid> _replacements;
    private readonly IDataFilter<ISoftDelete> _softDeleteFilter;

    public UtilityUsagePreviewAppService(
        IRepository<Room, Guid> rooms,
        IRepository<UtilityMeter, Guid> meters,
        IRepository<MeterReading, Guid> readings,
        IRepository<MeterReplacement, Guid> replacements,
        IDataFilter<ISoftDelete> softDeleteFilter)
    {
        _rooms = rooms;
        _meters = meters;
        _readings = readings;
        _replacements = replacements;
        _softDeleteFilter = softDeleteFilter;
    }

    [UnitOfWork(isTransactional: true)]
    public virtual async Task<MonthlyUsagePreviewDto> GetMonthlyUsagePreviewAsync(
        Guid roomId, UtilityType utilityType, int billingYear, int billingMonth)
    {
        if (roomId == Guid.Empty || !await _rooms.AnyAsync(x => x.Id == roomId))
            throw new BusinessException(BuildingManagementErrorCodes.RoomNotFound);

        if (!Enum.IsDefined(typeof(UtilityType), utilityType))
            throw new BusinessException(BuildingManagementErrorCodes.InvalidUtilityType);

        if (billingYear is < 1 or > 9999)
            throw new BusinessException(BuildingManagementErrorCodes.InvalidBillingYear);

        if (billingMonth is < 1 or > 12)
            throw new BusinessException(BuildingManagementErrorCodes.InvalidBillingMonth);

        var firstDay = new DateTime(billingYear, billingMonth, 1);
        var lastDay = new DateTime(
            billingYear, billingMonth, DateTime.DaysInMonth(billingYear, billingMonth));

        List<UtilityMeter> meters;
        List<MeterReading> readings;
        List<MeterReplacement> replacements;

        // Soft-deleted readings are intentionally included: a hidden reading
        // cannot silently reduce the recorded usage. The pure calculator
        // rejects such history for manual review.
        using (_softDeleteFilter.Disable())
        {
            var meterQuery = await _meters.GetQueryableAsync();
            meters = await AsyncExecuter.ToListAsync(
                meterQuery.Where(x => x.RoomId == roomId &&
                                      x.UtilityType == utilityType));

            var meterIds = meters.Select(x => x.Id).ToArray();

            if (meterIds.Length == 0)
            {
                readings = new List<MeterReading>();
            }
            else
            {
                var readingQuery = await _readings.GetQueryableAsync();
                readings = await AsyncExecuter.ToListAsync(
                    readingQuery.Where(x => meterIds.Contains(x.UtilityMeterId) &&
                                            (x.BillingYear < billingYear ||
                                             (x.BillingYear == billingYear &&
                                              x.BillingMonth <= billingMonth))));
            }

            var replacementQuery = await _replacements.GetQueryableAsync();
            replacements = await AsyncExecuter.ToListAsync(
                replacementQuery.Where(x => x.RoomId == roomId &&
                                            x.UtilityType == utilityType &&
                                            x.ReplacementDate >= firstDay &&
                                            x.ReplacementDate <= lastDay));
        }

        MonthlyMeterUsageBreakdown result;
        try
        {
            result = MonthlyMeterUsageCalculator.Calculate(
                roomId, utilityType, billingYear, billingMonth,
                meters, readings, replacements);
        }
        catch (InvalidOperationException ex)
        {
            throw new BusinessException(UtilityUsagePreviewErrorCodes.HistoryRequiresReview)
                .WithData("Details", ex.Message);
        }

        return new MonthlyUsagePreviewDto
        {
            RoomId = roomId,
            UtilityType = utilityType,
            BillingYear = billingYear,
            BillingMonth = billingMonth,
            RecordedUsage = result.RecordedUsage,
            CoverageIssues = MonthlyUsageCoverageAssessor.Assess(
                roomId, utilityType, billingYear, billingMonth,
                meters, readings, replacements).ToList(),
            Segments = result.Segments.Select(x => new MeterUsageSegmentDto
            {
                SourceId = x.SourceId,
                UtilityMeterId = x.UtilityMeterId,
                SourceType = x.SourceType.ToString(),
                RecordedDate = x.RecordedDate,
                PreviousReading = x.PreviousReading,
                CurrentReading = x.CurrentReading,
                Usage = x.Usage
            }).ToList()
        };
    }
}
