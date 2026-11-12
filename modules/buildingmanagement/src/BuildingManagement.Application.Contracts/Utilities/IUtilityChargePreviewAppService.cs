using System;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace BuildingManagement.Utilities;

public interface IUtilityChargePreviewAppService : IApplicationService
{
    /// <summary>
    /// Provides an indicative value only when one surviving rate covers
    /// the FULL billing month. Does not finalize usage or issue an invoice.
    /// Mid-month tariff changes require a separately approved allocation rule.
    /// </summary>
    Task<MonthlyChargePreviewDto> GetMonthlyChargePreviewAsync(
        Guid roomId, UtilityType utilityType, int billingYear, int billingMonth);
}
