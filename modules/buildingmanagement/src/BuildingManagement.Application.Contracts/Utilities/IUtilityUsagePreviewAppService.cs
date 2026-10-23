using System;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace BuildingManagement.Utilities;

public interface IUtilityUsagePreviewAppService : IApplicationService
{
    /// <summary>
    /// Preview RECORDED consumption for one room, utility type and billing month.
    /// This does not select utility rates or create/finalize an invoice.
    /// </summary>
    Task<MonthlyUsagePreviewDto> GetMonthlyUsagePreviewAsync(
        Guid roomId, UtilityType utilityType, int billingYear, int billingMonth);
}
