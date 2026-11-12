using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;
using Volo.Abp;

namespace BuildingManagement.Contracts
{
    public interface IContractAppService : IApplicationService
    {
        Task<ContractDto> GetAsync(Guid id);

        Task<List<ContractDto>> GetListAsync();

        Task<ContractDto> CreateAsync(
            CreateContractDto input
        );

        Task<ContractDto> SignAsync(Guid id);

        Task<ContractDto> RenewAsync(
            Guid id,
            RenewContractDto input
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

        // Direct End skips 2G.3 invoice, 2G.4 deposit and 2G.5 handover.
        // It must never be generated as a remote API.
        [RemoteService(IsEnabled = false)]
        Task<ContractDto> EndAsync(Guid id);

        Task<ContractDto> CancelAsync(Guid id);
    }
}