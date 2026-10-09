using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace BuildingManagement.Utilities
{
    public interface IMeterReadingAppService
        : IApplicationService
    {
        Task<MeterReadingDto> GetAsync(Guid id);

        Task<List<MeterReadingDto>> GetListAsync();

        Task<MeterReadingDto> CreateAsync(
            CreateMeterReadingDto input
        );

        Task<MeterReadingDto> UpdateAsync(
            Guid id,
            UpdateMeterReadingDto input
        );
    }
}