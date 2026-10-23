using System;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace BuildingManagement.Utilities;

public interface IMoveOutFinalMeterReadinessAppService : IApplicationService
{
    /// <summary>
    /// Checks final reading evidence for electricity and water against a
    /// MoveOutRequest's approved date. Never completes checklist or invoices.
    /// </summary>
    Task<MoveOutFinalMeterReadinessDto> GetAsync(Guid moveOutRequestId);
}
