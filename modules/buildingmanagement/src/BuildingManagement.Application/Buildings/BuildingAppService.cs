using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using BuildingManagement.Floors;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;

namespace BuildingManagement.Buildings
{
    public class BuildingAppService :
    ApplicationService,
    IBuildingAppService
    {
        private readonly IRepository<Building, Guid> _buildingRepository;
        private readonly IRepository<Floor, Guid> _floorRepository;

        public BuildingAppService(
            IRepository<Building, Guid> buildingRepository,
            IRepository<Floor, Guid> floorRepository)
        {
            _buildingRepository = buildingRepository;
            _floorRepository = floorRepository;
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
            var building =
                await _buildingRepository.FindAsync(id);

            if (building is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.BuildingNotFound
                );
            }

            await EnsureNoBuildingFloorsAsync(id);

            await _buildingRepository.DeleteAsync(
                building,
                autoSave: true
            );
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

        private async Task EnsureNoBuildingFloorsAsync(Guid buildingId)
        {
            var hasFloors = await _floorRepository.AnyAsync(
                f => f.BuildingId == buildingId
            );

            if (hasFloors)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.BuildingHasFloors
                );
            }
        }
    }
}
