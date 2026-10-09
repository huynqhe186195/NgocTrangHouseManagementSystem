using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace BuildingManagement.Utilities
{
    public interface IUtilityRateAppService
        : IApplicationService
    {
        Task<UtilityRateDto> GetAsync(Guid id);

        Task<List<UtilityRateDto>> GetListAsync();

        Task<UtilityRateDto> CreateAsync(
            CreateUtilityRateDto input
        );

        Task<UtilityRateDto> UpdateAsync(
            Guid id,
            UpdateUtilityRateDto input
        );

        Task DeleteAsync(Guid id);
    }
}
