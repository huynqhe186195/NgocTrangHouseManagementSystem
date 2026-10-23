using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.MoveOuts;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Uow;

namespace BuildingManagement.Utilities;

/// <summary>
/// Read-only bridge from Phase 2G.1's approved move-out date to Phase 2G.2
/// meter evidence. This does NOT complete FinalMeterReading checklist tasks,
/// end contracts, release rooms, finalize bills, or move deposits.
/// </summary>
public class MoveOutFinalMeterReadinessAppService
    : ApplicationService, IMoveOutFinalMeterReadinessAppService
{
    private readonly IRepository<MoveOutRequest, Guid> _requests;
    private readonly IRepository<UtilityMeter, Guid> _meters;
    private readonly IRepository<MeterReading, Guid> _readings;
    private readonly IRepository<MeterReplacement, Guid> _replacements;
    private readonly IUtilityUsagePreviewAppService _usagePreview;
    private readonly IDataFilter<ISoftDelete> _softDeleteFilter;

    public MoveOutFinalMeterReadinessAppService(
        IRepository<MoveOutRequest, Guid> requests,
        IRepository<UtilityMeter, Guid> meters,
        IRepository<MeterReading, Guid> readings,
        IRepository<MeterReplacement, Guid> replacements,
        IUtilityUsagePreviewAppService usagePreview,
        IDataFilter<ISoftDelete> softDeleteFilter)
    {
        _requests = requests;
        _meters = meters;
        _readings = readings;
        _replacements = replacements;
        _usagePreview = usagePreview;
        _softDeleteFilter = softDeleteFilter;
    }

    [UnitOfWork(isTransactional: true)]
    public virtual async Task<MoveOutFinalMeterReadinessDto> GetAsync(Guid moveOutRequestId)
    {
        var request = await _requests.FindAsync(moveOutRequestId);
        if (request is null)
            throw new BusinessException(MoveOutErrorCodes.NotFound);

        var result = new MoveOutFinalMeterReadinessDto
        {
            MoveOutRequestId = request.Id,
            ContractId = request.ContractId,
            RoomId = request.RoomId,
            RequestStatus = request.Status,
            ApprovedMoveOutDate = request.ApprovedMoveOutDate
        };

        if (request.Status != MoveOutRequestStatus.Approved ||
            !request.ApprovedMoveOutDate.HasValue)
        {
            foreach (var type in Enum.GetValues<UtilityType>())
                result.Utilities.Add(new MoveOutFinalMeterEvidenceDto
                {
                    UtilityType = type,
                    Status = "MoveOutNotApproved"
                });
            return result;
        }

        var date = request.ApprovedMoveOutDate.Value;

        // Fetch real historical physical-meter identities and their handovers.
        // Do not trust current IsActive as the state on a past move-out date.
        List<UtilityMeter> meters;
        List<MeterReading> readings;
        List<MeterReplacement> replacements;
        using (_softDeleteFilter.Disable())
        {
            var meterQuery = await _meters.GetQueryableAsync();
            meters = await AsyncExecuter.ToListAsync(
                meterQuery.Where(x => x.RoomId == request.RoomId));

            var meterIds = meters.Select(x => x.Id).ToArray();
            if (meterIds.Length == 0)
                readings = new List<MeterReading>();
            else
            {
                var readingQuery = await _readings.GetQueryableAsync();
                readings = await AsyncExecuter.ToListAsync(
                    readingQuery.Where(x =>
                        meterIds.Contains(x.UtilityMeterId) &&
                        x.BillingYear == date.Year &&
                        x.BillingMonth == date.Month));
            }

            var replacementQuery = await _replacements.GetQueryableAsync();
            replacements = await AsyncExecuter.ToListAsync(
                replacementQuery.Where(x => x.RoomId == request.RoomId));
        }

        foreach (var type in Enum.GetValues<UtilityType>())
        {
            MonthlyUsagePreviewDto usage;
            try
            {
                usage = await _usagePreview.GetMonthlyUsagePreviewAsync(
                    request.RoomId, type, date.Year, date.Month);
            }
            catch (BusinessException ex)
                when (ex.Code == UtilityUsagePreviewErrorCodes.HistoryRequiresReview)
            {
                result.Utilities.Add(new MoveOutFinalMeterEvidenceDto
                {
                    UtilityType = type,
                    Status = "UsageHistoryRequiresReview",
                    CoverageIssues = new List<string> { "UsageHistoryRequiresReview" }
                });
                continue;
            }

            var assessed = MoveOutFinalMeterEvidenceAssessor.Assess(
                request.RoomId, type, date, meters, readings, replacements,
                usage.CoverageIssues);

            result.Utilities.Add(new MoveOutFinalMeterEvidenceDto
            {
                UtilityType = type,
                UtilityMeterId = assessed.UtilityMeterId,
                MeterReadingId = assessed.MeterReadingId,
                FinalReading = assessed.FinalReading,
                Status = assessed.Status,
                CoverageIssues = usage.CoverageIssues.ToList()
            });
        }

        return result;
    }
}
