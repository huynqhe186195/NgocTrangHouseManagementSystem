using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using BuildingManagement.Floors;
using BuildingManagement.Security;
using System.Linq;
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
        private readonly IBuildingCrudAccessPolicy _access;
        private readonly BuildingOwnerBootstrapCoordinator _bootstrap;
        private readonly IBuildingScopedWriteBoundary _writeBoundary;

        public BuildingAppService(
            IRepository<Building, Guid> buildingRepository,
            IRepository<Floor, Guid> floorRepository,
            IBuildingCrudAccessPolicy access,
            BuildingOwnerBootstrapCoordinator bootstrap,
            IBuildingScopedWriteBoundary writeBoundary)
        {
            _buildingRepository = buildingRepository;
            _floorRepository = floorRepository;
            _access = access;
            _bootstrap = bootstrap;
            _writeBoundary = writeBoundary;
        }

        public async Task<BuildingDto> CreateAsync(CreateBuildingDto input)
        {
            // Owner and Building MUST be created atomically, not by a
            // second independent InsertAsync that can leave an orphan.
            // The bootstrap writer is default-DENY until reviewed DB
            // membership persistence is available.
            var building = await _bootstrap.CreateWithCurrentOwnerAsync(input);
            return ObjectMapper.Map<Building, BuildingDto>(building);
        }

        public async Task DeleteAsync(Guid id)
        {
            await _access.EnsureBuildingAsync(id, BuildingBusinessCapability.ManageBuilding);
            await _writeBoundary.ExecuteBuildingAsync(id, async () =>
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
            return true;
            });
        }

        public async Task<BuildingDto> GetAsync(Guid id)
        {
            await _access.EnsureBuildingAsync(id, BuildingBusinessCapability.ReadBuilding);
            var building =
                await _buildingRepository.GetAsync(id);

            return ObjectMapper.Map<
                Building,
                BuildingDto
            >(building);
        }

        public async Task<List<BuildingDto>> GetListAsync()
        {
            var authorized = await _access.GetAuthorizedBuildingIdsAsync(
                BuildingBusinessCapability.ReadBuilding);
            var buildings =
                await _buildingRepository.GetListAsync(b =>
                    authorized.Contains(b.Id));

            return ObjectMapper.Map<
                List<Building>,
                List<BuildingDto>
            >(buildings);
        }

        public async Task<BuildingDto> UpdateAsync(
    Guid id,
    UpdateBuildingDto input)
        {
            await _access.EnsureBuildingAsync(id, BuildingBusinessCapability.ManageBuilding);
            return await _writeBoundary.ExecuteBuildingAsync(id, async () =>
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
            });
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
