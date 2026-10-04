using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;

namespace BuildingManagement.Buildings
{
    public class BuildingAppService :
    ApplicationService,
    IBuildingAppService
    {
        private readonly IRepository<Building, Guid>? _buildingRepository;

        public BuildingAppService(IRepository<Building, Guid>? buildingRepository)
        {
            _buildingRepository = buildingRepository;
        }

        public async Task<BuildingDto> CreateAsync(CreateBuildingDto input)
        {
            var building = new Building(
                GuidGenerator.Create(),
                input.Name,
                input.Address,
                input.Description
            );


            await _buildingRepository.InsertAsync(
                building
            );


            return ObjectMapper.Map<Building, BuildingDto>(
                building
            );
        }

        public async Task DeleteAsync(Guid id)
        {
            await _buildingRepository.DeleteAsync(id, autoSave: true);
        }

        public async Task<BuildingDto> GetAsync(Guid id)
        {
            var building =
                await _buildingRepository.GetAsync(id);

            return ObjectMapper.Map<
                Building,
                BuildingDto
            >(building);
        }

        public async Task<List<BuildingDto>> GetListAsync()
        {
            var buildings =
                await _buildingRepository.GetListAsync();

            return ObjectMapper.Map<
                List<Building>,
                List<BuildingDto>
            >(buildings);
        }

        public async Task<BuildingDto> UpdateAsync(
    Guid id,
    UpdateBuildingDto input)
        {
            var building =
                await _buildingRepository.GetAsync(id);


            building.Update(
                input.Name,
                input.Address,
                input.Description
            );


            await _buildingRepository.UpdateAsync(
                building
            );


            return ObjectMapper.Map<
                Building,
                BuildingDto
            >(building);
        }
    }
}
