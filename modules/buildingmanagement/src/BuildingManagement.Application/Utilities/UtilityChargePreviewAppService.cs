using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Floors;
using BuildingManagement.Rooms;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Uow;

namespace BuildingManagement.Utilities;

/// <summary>
/// Tariff COVERAGE check only; intentionally cannot allocate consumption
/// across mid-month tariff changes (monthly readings contain no reliable
/// per-day consumption). Amounts are provisional and never billed here.
/// </summary>
public class UtilityChargePreviewAppService
    : ApplicationService, IUtilityChargePreviewAppService
{
    private readonly IRepository<Room, Guid> _rooms;
    private readonly IRepository<Floor, Guid> _floors;
    private readonly IRepository<UtilityRate, Guid> _rates;
    private readonly IUtilityUsagePreviewAppService _usagePreview;
    private readonly IDataFilter<ISoftDelete> _softDeleteFilter;

    public UtilityChargePreviewAppService(
        IRepository<Room, Guid> rooms,
        IRepository<Floor, Guid> floors,
        IRepository<UtilityRate, Guid> rates,
        IUtilityUsagePreviewAppService usagePreview,
        IDataFilter<ISoftDelete> softDeleteFilter)
    {
        _rooms = rooms;
        _floors = floors;
        _rates = rates;
        _usagePreview = usagePreview;
        _softDeleteFilter = softDeleteFilter;
    }

    [UnitOfWork(isTransactional: true)]
    public virtual async Task<MonthlyChargePreviewDto> GetMonthlyChargePreviewAsync(
        Guid roomId, UtilityType utilityType, int billingYear, int billingMonth)
    {
        // Perform the existing room/month/history validation first.
        // It refuses soft-deleted readings instead of hiding lost consumption.
        var usage = await _usagePreview.GetMonthlyUsagePreviewAsync(
            roomId, utilityType, billingYear, billingMonth);

        var room = await _rooms.GetAsync(roomId);
        var floor = await _floors.GetAsync(room.FloorId);
        var buildingId = floor.BuildingId;

        var firstDay = new DateTime(billingYear, billingMonth, 1);
        var lastDay = new DateTime(
            billingYear, billingMonth, DateTime.DaysInMonth(billingYear, billingMonth));

        List<UtilityRate> monthlyRates;
        // Historical deleted rates might already have been referenced by a
        // bill or a meter reading. Do not silently substitute another rate.
        using (_softDeleteFilter.Disable())
        {
            var query = await _rates.GetQueryableAsync();
            monthlyRates = await AsyncExecuter.ToListAsync(
                query.Where(x => x.BuildingId == buildingId &&
                                 x.UtilityType == utilityType &&
                                 x.EffectiveFrom <= lastDay &&
                                 (x.EffectiveTo == null ||
                                  x.EffectiveTo.Value >= firstDay)));
        }

        var preview = new MonthlyChargePreviewDto
        {
            RoomId = roomId,
            BuildingId = buildingId,
            UtilityType = utilityType,
            BillingYear = billingYear,
            BillingMonth = billingMonth,
            RecordedUsage = usage.RecordedUsage,
            UsagePreview = usage
        };

        if (monthlyRates.Any(x => x.IsDeleted))
        {
            preview.RateStatus = "DeletedRateHistoryRequiresReview";
            return preview;
        }

        if (monthlyRates.Count == 0)
        {
            preview.RateStatus = "NoRate";
            return preview;
        }

        if (monthlyRates.Count != 1)
        {
            // Never estimate a split by time; actual daily usage is unknown.
            preview.RateStatus = "MultipleRatesInMonth";
            return preview;
        }

        var rate = monthlyRates[0];
        if (rate.EffectiveFrom.Date > firstDay ||
            (rate.EffectiveTo.HasValue && rate.EffectiveTo.Value.Date < lastDay))
        {
            preview.RateStatus = "IncompleteRateCoverage";
            return preview;
        }

        if (rate.UnitPrice <= 0)
        {
            preview.RateStatus = "IncompleteRateCoverage";
            return preview;
        }

        preview.RateStatus = "SingleRateWholeMonth";
        preview.UtilityRateId = rate.Id;
        preview.UnitPrice = rate.UnitPrice;

        // Do not quote a misleading amount if the monthly counter history
        // is incomplete, even when a price covers the whole month.
        // Inspect UsagePreview.CoverageIssues for the reason. Never infer
        // that a missing monthly reading means zero chargeable consumption.
        if (!usage.IsSafeForProvisionalPricing)
            return preview;

        // No rounding until a separately approved invoice/currency policy.
        preview.ProvisionalAmount = usage.RecordedUsage * rate.UnitPrice;
        return preview;
    }
}
