using BuildingManagement.Buildings;
using BuildingManagement.Rooms;
using BuildingManagement.Security;
using Volo.Abp.Authorization;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Guids;
using Volo.Abp.ObjectMapping;

namespace BuildingManagement.Floors
{
    public class FloorAppService :
        ApplicationService,
        IFloorAppService
    {
        private readonly IRepository<Floor, Guid> _floorRepository;
        private readonly IRepository<Building, Guid> _buildingRepository;
        private readonly IRepository<Room, Guid> _roomRepository;
        private readonly IRepository<FloorLayoutItem, Guid> _layoutItemRepository;
        private readonly IBuildingCrudAccessPolicy _access;
        private readonly IBuildingBusinessResourceScopeReader _resourceScope;
        private readonly IBuildingScopedWriteBoundary _writes;

        public FloorAppService(
            IRepository<Floor, Guid> floorRepository,
            IRepository<Building, Guid> buildingRepository,
            IRepository<Room, Guid> roomRepository,
            IRepository<FloorLayoutItem, Guid> layoutItemRepository,
            IBuildingCrudAccessPolicy access,
            IBuildingBusinessResourceScopeReader resourceScope,
            IBuildingScopedWriteBoundary writes)
        {
            _floorRepository = floorRepository;
            _buildingRepository = buildingRepository;
            _roomRepository = roomRepository;
            _layoutItemRepository = layoutItemRepository;
            _access = access;
            _resourceScope = resourceScope;
            _writes = writes;
        }

        public async Task<FloorDto> CreateAsync(
            CreateFloorDto input)
        {
            await _access.EnsureBuildingAsync(
                input.BuildingId, BuildingBusinessCapability.ManageFloor);
            return await _writes.ExecuteBuildingAsync(input.BuildingId, async () =>
            {
                // Parent checks, uniqueness and inserts run after the lock.
                await EnsureBuildingExistsAsync(input.BuildingId);
                await EnsureFloorNumberUniqueAsync(
                    input.BuildingId, input.FloorNumber);
                var created = new Floor(GuidGenerator.Create(), input.BuildingId,
                    input.FloorNumber, input.Name, input.Description);
                await _floorRepository.InsertAsync(created, autoSave: true);
                return ObjectMapper.Map<Floor, FloorDto>(created);
            }, BuildingScopedMutationKind.Floor);
        }

        public async Task<List<FloorDto>> GetListAsync()
        {
            var authorized = await _access.GetAuthorizedBuildingIdsAsync(
                BuildingBusinessCapability.ReadFloor);
            var floorQuery = await _floorRepository.GetQueryableAsync();
            var buildings = await _buildingRepository.GetQueryableAsync();
            var scoped = from floor in floorQuery
                         join building in buildings
                             on floor.BuildingId equals building.Id
                         where authorized.Contains(building.Id)
                             && !building.IsDeleted && !floor.IsDeleted
                         select floor;
            var floors = await AsyncExecuter.ToListAsync(scoped);

            return ObjectMapper.Map<
                List<Floor>,
                List<FloorDto>
            >(floors);
        }

        public async Task<FloorDto> GetAsync(
            Guid id)
        {
            await _access.EnsureResourceAsync(
                BuildingBusinessResourceKind.Floor, id,
                BuildingBusinessCapability.ReadFloor);
            var floor =
                await _floorRepository.GetAsync(id);

            return ObjectMapper.Map<
                Floor,
                FloorDto
            >(floor);
        }

        public async Task<FloorDto> UpdateAsync(
            Guid id,
            UpdateFloorDto input)
        {
            await _access.EnsureResourceAsync(
                BuildingBusinessResourceKind.Floor, id,
                BuildingBusinessCapability.ManageFloor);
            var buildingId = await ResolveBuildingAsync(
                BuildingBusinessResourceKind.Floor, id);
            return await _writes.ExecuteBuildingAsync(buildingId, async () =>
            {
                var floor = await _floorRepository.GetAsync(id);
                if (floor.BuildingId != buildingId)
                    throw new AbpAuthorizationException("Floor scope changed.");
                await EnsureFloorNumberUniqueAsync(
                    buildingId, input.FloorNumber, floor.Id);
                floor.Update(input.FloorNumber, input.Name, input.Description);
                await _floorRepository.UpdateAsync(floor, autoSave: true);
                return ObjectMapper.Map<Floor, FloorDto>(floor);
            }, BuildingScopedMutationKind.Floor);
        }

        public async Task DeleteAsync(Guid id)
        {
            await _access.EnsureResourceAsync(
                BuildingBusinessResourceKind.Floor, id,
                BuildingBusinessCapability.ManageFloor);
            var buildingId = await ResolveBuildingAsync(
                BuildingBusinessResourceKind.Floor, id);
            await _writes.ExecuteBuildingAsync(buildingId, async () =>
            {
                var floor = await _floorRepository.FindAsync(id)
                    ?? throw new BusinessException(
                        BuildingManagementErrorCodes.FloorNotFound);
                if (floor.BuildingId != buildingId)
                    throw new AbpAuthorizationException("Floor scope changed.");
                await EnsureNoFloorRoomsAsync(id);
                await _floorRepository.DeleteAsync(floor, autoSave: true);
                return true;
            }, BuildingScopedMutationKind.Floor);
        }

        public async Task<List<FloorLayoutItemDto>> GetLayoutItemsAsync(Guid id)
        {
            await _access.EnsureResourceAsync(
                BuildingBusinessResourceKind.Floor, id,
                BuildingBusinessCapability.ReadFloor);
            var items = await _layoutItemRepository.GetListAsync(
                x => x.FloorId == id);
            return ObjectMapper.Map<List<FloorLayoutItem>, List<FloorLayoutItemDto>>(
                items.ToList());
        }

        public async Task<FloorLayoutItemDto> CreateLayoutItemAsync(
            Guid id, CreateFloorLayoutItemDto input)
        {
            await _access.EnsureResourceAsync(
                BuildingBusinessResourceKind.Floor, id,
                BuildingBusinessCapability.ManageFloor);
            var buildingId = await ResolveBuildingAsync(
                BuildingBusinessResourceKind.Floor, id);
            return await _writes.ExecuteBuildingAsync(buildingId, async () =>
            {
                var floor = await _floorRepository.GetAsync(id);
                if (floor.BuildingId != buildingId)
                    throw new AbpAuthorizationException("Floor scope changed.");
                ValidateLayoutItem(input.Kind, input.X, input.Y,
                    input.Width, input.Height);
                var created = new FloorLayoutItem(GuidGenerator.Create(), id,
                    input.Kind, input.Label, input.X, input.Y,
                    input.Width, input.Height);
                await _layoutItemRepository.InsertAsync(created, autoSave: true);
                return ObjectMapper.Map<FloorLayoutItem, FloorLayoutItemDto>(
                    created);
            }, BuildingScopedMutationKind.Floor);
        }

        public async Task<FloorLayoutItemDto> UpdateLayoutItemAsync(
            Guid id, Guid itemId, UpdateFloorLayoutItemDto input)
        {
            await _access.EnsureResourceAsync(
                BuildingBusinessResourceKind.Floor, id,
                BuildingBusinessCapability.ManageFloor);
            var buildingId = await ResolveBuildingAsync(
                BuildingBusinessResourceKind.Floor, id);
            return await _writes.ExecuteBuildingAsync(buildingId, async () =>
            {
                var floor = await _floorRepository.GetAsync(id);
                if (floor.BuildingId != buildingId)
                    throw new AbpAuthorizationException("Floor scope changed.");
                var item = await _layoutItemRepository.FindAsync(itemId);
                if (item is null || item.FloorId != id)
                    throw new BusinessException(
                        BuildingManagementErrorCodes.FloorLayoutItemNotFound);
                ValidateLayoutItem(input.Kind, input.X, input.Y,
                    input.Width, input.Height);
                item.Update(input.Kind, input.Label, input.X, input.Y,
                    input.Width, input.Height);
                await _layoutItemRepository.UpdateAsync(item, autoSave: true);
                return ObjectMapper.Map<FloorLayoutItem, FloorLayoutItemDto>(item);
            }, BuildingScopedMutationKind.Floor);
        }

        public async Task DeleteLayoutItemAsync(Guid id, Guid itemId)
        {
            await _access.EnsureResourceAsync(
                BuildingBusinessResourceKind.Floor, id,
                BuildingBusinessCapability.ManageFloor);
            var buildingId = await ResolveBuildingAsync(
                BuildingBusinessResourceKind.Floor, id);
            await _writes.ExecuteBuildingAsync(buildingId, async () =>
            {
                var item = await _layoutItemRepository.FindAsync(itemId);
                if (item is null || item.FloorId != id)
                    throw new BusinessException(
                        BuildingManagementErrorCodes.FloorLayoutItemNotFound);
                await _layoutItemRepository.DeleteAsync(item, autoSave: true);
                return true;
            }, BuildingScopedMutationKind.Floor);
        }

        private static void ValidateLayoutItem(
            FloorLayoutItemKind kind,
            decimal x, decimal y, decimal width, decimal height)
        {
            if (!Enum.IsDefined(kind) ||
                width < 0 || height < 0 ||
                width > 10000 || height > 10000 ||
                x < -10000 || x > 10000 || y < -10000 || y > 10000)
                throw new BusinessException(
                    BuildingManagementErrorCodes.InvalidFloorLayoutItem);
        }

        private async Task<Guid> ResolveBuildingAsync(
            BuildingBusinessResourceKind kind, Guid resourceId)
        {
            var scope = await _resourceScope.ReadAsync(kind, resourceId);
            if (scope is null || scope.ResourceId != resourceId ||
                scope.BuildingId == Guid.Empty)
                throw new AbpAuthorizationException("Floor/Building scope unavailable.");
            return scope.BuildingId;
        }

        private async Task EnsureNoFloorRoomsAsync(Guid floorId)
        {
            var hasRooms = await _roomRepository.AnyAsync(
                r => r.FloorId == floorId
            );

            if (hasRooms)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.FloorHasRooms
                );
            }
        }

        private async Task EnsureFloorNumberUniqueAsync(Guid buildingId, int floorNumber ,Guid? excludedFloorId = null)
        {
            var exists = await _floorRepository.AnyAsync(floor =>
                floor.BuildingId == buildingId
                && floor.FloorNumber == floorNumber
                && (!excludedFloorId.HasValue
                    || floor.Id != excludedFloorId.Value)
            );

            if (exists)
            {
                throw new BusinessException(
                        BuildingManagementErrorCodes.FloorNumberAlreadyExists)
                    .WithData("FloorNumber", floorNumber);
            }
        }

        private async Task EnsureBuildingExistsAsync(
    Guid buildingId)
        {
            var building =
                await _buildingRepository.FindAsync(buildingId);

            if (building is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.BuildingNotFound
                );
            }
        }
    }
}
