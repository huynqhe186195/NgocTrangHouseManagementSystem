using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace BuildingManagement.Contracts
{
    /// <summary>
    /// Read-only contract surface for the admin screen. Deliberately narrower
    /// than <see cref="IContractAppService"/>: that service writes contracts and
    /// is not HTTP-exposed. Nothing here mutates, so no contract business rule
    /// is reachable through it.
    /// </summary>
    public interface IContractsAppService : IApplicationService
    {
        Task<List<ContractSummaryDto>> GetListAsync();

        Task<ContractSummaryDto> GetAsync(Guid id);
    }
}
