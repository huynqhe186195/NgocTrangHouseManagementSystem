using BuildingManagement.Contracts;
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

        public RoomAppService(
    IRepository<Room, Guid> roomRepository,
    IRepository<Floor, Guid> floorRepository,
    IRepository<Contract, Guid> contractRepository,
    IRepository<UtilityMeter, Guid> utilityMeterRepository)
        {
            _roomRepository = roomRepository;
            _floorRepository = floorRepository;
            _contractRepository = contractRepository;
            _utilityMeterRepository = utilityMeterRepository;
        }

        public async Task<RoomDto> CreateAsync(
            CreateRoomDto input)
        {
            var floor = await GetExistingFloorAsync(
                input.FloorId
            );

            ValidateRoomNumber(
                floor.FloorNumber,
                input.RoomNumber
            );

            await EnsureRoomNumberUniqueAsync(input.FloorId, input.RoomNumber);

            var room = new Room(
                GuidGenerator.Create(),
                input.FloorId,
                input.RoomNumber,
                input.Name,
                input.Area,
                input.Capacity,
                input.Status,
                input.Description
            );

            await _roomRepository.InsertAsync(room);

            return ObjectMapper.Map<Room, RoomDto>(
                room
            );
        }

        public async Task<RoomDto> GetAsync(Guid id)
        {
            var room =
                await GetExistingRoomAsync(id);

            return ObjectMapper.Map<
                Room,
                RoomDto
            >(room);
        }

        public async Task<List<RoomDto>> GetListAsync()
        {
            var rooms =
                await _roomRepository.GetListAsync();

            return ObjectMapper.Map<
                List<Room>,
                List<RoomDto>
            >(rooms);
        }

        public async Task<RoomDto> UpdateAsync(
            Guid id,
            UpdateRoomDto input)
        {
            var room =
                await GetExistingRoomAsync(id);

            var floor =
                await GetExistingFloorAsync(
                    room.FloorId
                );

            ValidateRoomNumber(
                floor.FloorNumber,
                input.RoomNumber
            );

            await EnsureRoomNumberUniqueAsync(room.FloorId, input.RoomNumber, room.Id);

            room.Update(
                input.RoomNumber,
                input.Name,
                input.Area,
                input.Capacity,
                input.Status,
                input.Description
            );

            await _roomRepository.UpdateAsync(room);

            return ObjectMapper.Map<
                Room,
                RoomDto
            >(room);
        }

        public async Task DeleteAsync(Guid id)
        {
            var room =
                await GetExistingRoomAsync(id);

            var hasActiveContract =
                await _contractRepository.AnyAsync(
                    x =>
                        x.RoomId == id
                        &&
                        x.Status == ContractStatus.Active
                );

            if (hasActiveContract)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .RoomHasActiveContract
                );
            }

            var hasActiveUtilityMeter =
                await _utilityMeterRepository.AnyAsync(
                    x =>
                        x.RoomId == id
                        &&
                        x.IsActive
                );

            if (hasActiveUtilityMeter)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .RoomHasActiveUtilityMeter
                );
            }

            var hasAnyContract =
                await _contractRepository.AnyAsync(
                    x => x.RoomId == id
                );

            var hasAnyUtilityMeter =
                await _utilityMeterRepository.AnyAsync(
                    x => x.RoomId == id
                );

            if (hasAnyContract ||
                hasAnyUtilityMeter)
            {
                room.ChangeStatus(
                    RoomStatus.Inactive
                );

                await _roomRepository.UpdateAsync(
                    room,
                    autoSave: true
                );

                return;
            }

            await _roomRepository.DeleteAsync(
                room,
                autoSave: true
            );
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
