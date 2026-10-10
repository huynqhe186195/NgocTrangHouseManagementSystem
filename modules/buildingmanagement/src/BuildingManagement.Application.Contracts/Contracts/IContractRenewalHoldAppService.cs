using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace BuildingManagement.Contracts
{
    public interface IContractRenewalHoldAppService
        : IApplicationService
    {
        Task<ContractRenewalHoldDto> GetAsync(Guid id);

        Task<List<ContractRenewalHoldDto>> GetListAsync();

        Task<ContractRenewalHoldDto> CreateAsync(
            CreateContractRenewalHoldDto input
        );

        Task<ContractRenewalHoldDto> CancelAsync(Guid id);
    }
}