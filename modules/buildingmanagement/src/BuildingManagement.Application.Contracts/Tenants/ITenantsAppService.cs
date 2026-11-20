using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace BuildingManagement.Tenants
{
    /// <summary>
    /// The admin roster read. Reads only — <see cref="ITenantAppService"/>
    /// owns the writes and stays HTTP-denied.
    /// </summary>
    public interface ITenantsAppService : IApplicationService
    {
        Task<List<TenantSummaryDto>> GetListAsync();

        Task<TenantSummaryDto> GetAsync(Guid id);
    }
}
