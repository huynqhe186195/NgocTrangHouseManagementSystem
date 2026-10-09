using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace BuildingManagement.Contracts
{
    public interface IContractAppService : IApplicationService
    {
        Task<ContractDto> GetAsync(Guid id);

        Task<List<ContractDto>> GetListAsync();

        Task<ContractDto> CreateAsync(
            CreateContractDto input
        );

        Task<ContractDto> UpdateAsync(
            Guid id,
            UpdateContractDto input
        );

        Task<List<ContractTenantDto>> GetTenantsAsync(
            Guid id
        );

        Task<ContractTenantDto> AddTenantAsync(
            Guid id,
            AddContractTenantDto input
        );

        Task<ContractTenantDto> UpdateTenantRoleAsync(
            Guid id,
            Guid tenantId,
            UpdateContractTenantRoleDto input
        );

        Task RemoveTenantAsync(
            Guid id,
            Guid tenantId
        );

        Task<ContractDto> ActivateAsync(Guid id);

        Task<ContractDto> EndAsync(Guid id);

        Task<ContractDto> CancelAsync(Guid id);
    }
}