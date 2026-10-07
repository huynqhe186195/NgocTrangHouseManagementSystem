using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace BuildingManagement.Floors
{
    public interface IFloorAppService : IApplicationService
    {
        Task<FloorDto> CreateAsync(
            CreateFloorDto input);

        Task<List<FloorDto>> GetListAsync();

        Task<FloorDto> GetAsync(
            Guid id);

        Task<FloorDto> UpdateAsync(
            Guid id,
            UpdateFloorDto input);

        Task DeleteAsync(
            Guid id);
    }
}
