using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace BuildingManagement.Utilities
{
    public interface IUtilityMeterAppService
        : IApplicationService
    {
        Task<UtilityMeterDto> GetAsync(Guid id);

        Task<List<UtilityMeterDto>> GetListAsync();

        Task<UtilityMeterDto> CreateAsync(
            CreateUtilityMeterDto input
        );

        Task<UtilityMeterDto> UpdateAsync(
            Guid id,
            UpdateUtilityMeterDto input
        );

        Task<UtilityMeterDto> DeactivateAsync(
            Guid id
        );

        Task DeleteAsync(Guid id);
    }
}