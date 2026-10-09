using Abp.Application.Services;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace BuildingManagement.Tenants
{
    public interface ITenantAppService : IApplicationService
    {
        Task<TenantDto> GetAsync(Guid id);

        Task<List<TenantDto>> GetListAsync();

        Task<TenantDto> CreateAsync(
            CreateTenantDto input);

        Task<TenantDto> UpdateAsync(
            Guid id,
            UpdateTenantDto input);

        Task DeleteAsync(Guid id);
    }
}
