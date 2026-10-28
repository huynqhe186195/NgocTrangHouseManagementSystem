using BuildingManagement.Contracts;
using BuildingManagement.Buildings;
using BuildingManagement.Security;
using Volo.Abp.Authorization;
using System.Linq;
using BuildingManagement.Floors;
using BuildingManagement.Utilities;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Guids;
using Volo.Abp.ObjectMapping;

namespace BuildingManagement.Rooms
{
    public class RoomAppService :
       ApplicationService,
       IRoomAppService
    {
        private readonly IRepository<Room, Guid> _roomRepository;
        private readonly IRepository<Floor, Guid> _floorRepository;
        private readonly IRepository<Contract, Guid>_contractRepository;
        private readonly IRepository<UtilityMeter, Guid> _utilityMeterRepository;
        private readonly IRepository<Building, Guid> _buildingRepository;
        private readonly IBuildingCrudAccessPolicy _access;
        private readonly IBuildingBusinessResourceScopeReader _resourceScope;
        private readonly IBuildingScopedWriteBoundary _writes;

        public RoomAppService(
    IRepository<Room, Guid> roomRepository,
    IRepository<Floor, Guid> floorRepository,
    IRepository<Contract, Guid> contractRepository,
    IRepository<UtilityMeter, Guid> utilityMeterRepository,
    IRepository<Building, Guid> buildingRepository,
    IBuildingCrudAccessPolicy access,
    IBuildingBusinessResourceScopeReader resourceScope,
    IBuildingScopedWriteBoundary writes)
        {
            _roomRepository = roomRepository;
            _floorRepository = floorRepository;
            _contractRepository = contractRepository;
            _utilityMeterRepository = utilityMeterRepository;
            _buildingRepository = buildingRepository;
            _access = access;
            _resourceScope = resourceScope;
            _writes = writes;
        }

        public async Task<RoomDto> CreateAsync(
            CreateRoomDto input)
        {
            await _access.EnsureResourceAsync(
                BuildingBusinessResourceKind.Floor, input.FloorId,
                BuildingBusinessCapability.ManageRoom);
            var buildingId = await ResolveBuildingAsync(
                BuildingBusinessResourceKind.Floor, input.FloorId);
            return await _writes.ExecuteBuildingAsync(buildingId, async () =>
            {
                var floor = await GetExistingFloorAsync(input.FloorId);
                if (floor.BuildingId != buildingId)
                    throw new AbpAuthorizationException("Room parent moved.");
                ValidateRoomNumber(floor.FloorNumber, input.RoomNumber);
                await EnsureRoomNumberUniqueAsync(input.FloorId, input.RoomNumber);
                var room = new Room(GuidGenerator.Create(), input.FloorId,
                    input.RoomNumber, input.Name, input.Area, input.Capacity,
                    input.Status, input.Description);
                await _roomRepository.InsertAsync(room, autoSave: true);
                return ObjectMapper.Map<Room, RoomDto>(room);
            });
        }

        public async Task<RoomDto> GetAsync(Guid id)
        {
            await _access.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room, id,
                BuildingBusinessCapability.ReadRoom);
            var room =
                await GetExistingRoomAsync(id);

            return ObjectMapper.Map<
                Room,
                RoomDto
            >(room);
        }

        public async Task<List<RoomDto>> GetListAsync()
        {
            var authorized = await _access.GetAuthorizedBuildingIdsAsync(
                BuildingBusinessCapability.ReadRoom);
            var roomsQuery = await _roomRepository.GetQueryableAsync();
            var floors = await _floorRepository.GetQueryableAsync();
            var buildings = await _buildingRepository.GetQueryableAsync();
            var scoped = from room in roomsQuery
                         join floor in floors on room.FloorId equals floor.Id
                         join building in buildings
                             on floor.BuildingId equals building.Id
                         where authorized.Contains(building.Id)
                             && !building.IsDeleted && !floor.IsDeleted
                             && !room.IsDeleted
                         select room;
            var rooms = await AsyncExecuter.ToListAsync(scoped);

            return ObjectMapper.Map<
                List<Room>,
                List<RoomDto>
            >(rooms);
        }

        public async Task<RoomDto> UpdateAsync(
            Guid id,
            UpdateRoomDto input)
        {
            await _access.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room, id,
                BuildingBusinessCapability.ManageRoom);
            var buildingId = await ResolveBuildingAsync(
                BuildingBusinessResourceKind.Room, id);
            return await _writes.ExecuteBuildingAsync(buildingId, async () =>
            {
                var room = await GetExistingRoomAsync(id);
                var floor = await GetExistingFloorAsync(room.FloorId);
                if (floor.BuildingId != buildingId)
                    throw new AbpAuthorizationException("Room scope changed.");
                ValidateRoomNumber(floor.FloorNumber, input.RoomNumber);
                await EnsureRoomNumberUniqueAsync(room.FloorId,
                    input.RoomNumber, room.Id);
                room.Update(input.RoomNumber, input.Name, input.Area,
                    input.Capacity, input.Status, input.Description);
                await _roomRepository.UpdateAsync(room, autoSave: true);
                return ObjectMapper.Map<Room, RoomDto>(room);
            });
        }

        public async Task DeleteAsync(Guid id)
        {
            await _access.EnsureResourceAsync(
                BuildingBusinessResourceKind.Room, id,
                BuildingBusinessCapability.ManageRoom);
            var buildingId = await ResolveBuildingAsync(
                BuildingBusinessResourceKind.Room, id);
            await _writes.ExecuteBuildingAsync(buildingId, async () =>
            {
                var room = await GetExistingRoomAsync(id);
                var floor = await GetExistingFloorAsync(room.FloorId);
                if (floor.BuildingId != buildingId)
                    throw new AbpAuthorizationException("Room scope changed.");
                if (await _contractRepository.AnyAsync(x =>
                    x.RoomId == id && x.Status == ContractStatus.Active))
                    throw new BusinessException(
                        BuildingManagementErrorCodes.RoomHasActiveContract);
                if (await _utilityMeterRepository.AnyAsync(x =>
                    x.RoomId == id && x.IsActive))
                    throw new BusinessException(
                        BuildingManagementErrorCodes.RoomHasActiveUtilityMeter);
                var hasContract = await _contractRepository.AnyAsync(
                    x => x.RoomId == id);
                var hasMeter = await _utilityMeterRepository.AnyAsync(
                    x => x.RoomId == id);
                if (hasContract || hasMeter)
                {
                    room.ChangeStatus(RoomStatus.Inactive);
                    await _roomRepository.UpdateAsync(room, autoSave: true);
                }
                else
                {
                    await _roomRepository.DeleteAsync(room, autoSave: true);
                }
                return true;
            });
        }

        private async Task<Guid> ResolveBuildingAsync(
            BuildingBusinessResourceKind kind, Guid resourceId)
        {
            var scope = await _resourceScope.ReadAsync(kind, resourceId);
            if (scope is null || scope.ResourceId != resourceId ||
                scope.BuildingId == Guid.Empty)
                throw new AbpAuthorizationException("Room/Building scope unavailable.");
            return scope.BuildingId;
        }

        // floor 1 -> room 101, room 102 || floor 2 -> room 201, room 202
        private static void ValidateRoomNumber(
            int floorNumber,
            string roomNumber)
        {
            if (!int.TryParse(roomNumber, out _))
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.InvalidRoomNumber
                )
                .WithData("RoomNumber", roomNumber);
            }

            if (roomNumber.Length < 3)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.InvalidRoomNumber
                )
                .WithData("RoomNumber", roomNumber);
            }

            var floorPart = roomNumber[..^2];

            if (!int.TryParse(floorPart, out var roomFloorNumber) ||
                roomFloorNumber != floorNumber)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.RoomNumberDoesNotMatchFloor
                )
                .WithData("FloorNumber", floorNumber)
                .WithData("RoomNumber", roomNumber);
            }
        }

        private async Task EnsureRoomNumberUniqueAsync(Guid floorId, string roomNumber, Guid? excludedRoomId = null)
        {
            bool exists;

            if (excludedRoomId.HasValue)
            {
                exists = await _roomRepository.AnyAsync(
                    x => x.FloorId == floorId
                         && x.RoomNumber == roomNumber
                         && x.Id != excludedRoomId.Value
                );
            }
            else
            {
                exists = await _roomRepository.AnyAsync(
                    x => x.FloorId == floorId
                         && x.RoomNumber == roomNumber
                );
            }

            if (exists)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.RoomNumberAlreadyExists
                )
                .WithData("FloorId", floorId)
                .WithData("RoomNumber", roomNumber);
            }
        }

        private async Task<Floor> GetExistingFloorAsync(
    Guid floorId)
        {
            var floor =
                await _floorRepository.FindAsync(floorId);

            if (floor is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.FloorNotFound
                );
            }

            return floor;
        }

        private async Task<Room> GetExistingRoomAsync(Guid roomId)
        {
            var room =
                await _roomRepository.FindAsync(roomId);

            if (room is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.RoomNotFound
                );
            }

            return room;
        }


    }
}
