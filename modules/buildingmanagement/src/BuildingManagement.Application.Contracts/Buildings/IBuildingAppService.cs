using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace BuildingManagement.Buildings
{
    public interface IBuildingAppService : IApplicationService
    {
        Task<BuildingDto> CreateAsync(
            CreateBuildingDto input);

        Task<List<BuildingDto>> GetListAsync();

        Task<BuildingDto> GetAsync(
            Guid id);

        Task<BuildingDto> UpdateAsync(
            Guid id,
            UpdateBuildingDto input);

        Task DeleteAsync(
            Guid id);
    }
}
