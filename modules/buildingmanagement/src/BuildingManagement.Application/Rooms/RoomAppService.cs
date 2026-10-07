using BuildingManagement.Floors;
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

        public RoomAppService(
            IRepository<Room, Guid> roomRepository,
            IRepository<Floor, Guid> floorRepository)
        {
            _roomRepository = roomRepository;
            _floorRepository = floorRepository;
        }

        public async Task<RoomDto> CreateAsync(
            CreateRoomDto input)
        {
            var floor = await _floorRepository.GetAsync(
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
                await _roomRepository.GetAsync(id);

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
                await _roomRepository.GetAsync(id);

            var floor =
                await _floorRepository.GetAsync(
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
            await _roomRepository.DeleteAsync(
                id,
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
                    "BuildingManagement:InvalidRoomNumber"
                )
                .WithData("RoomNumber", roomNumber);
            }

            if (roomNumber.Length < 3)
            {
                throw new BusinessException(
                    "BuildingManagement:InvalidRoomNumber"
                )
                .WithData("RoomNumber", roomNumber);
            }

            var floorPart = roomNumber[..^2];

            if (!int.TryParse(floorPart, out var roomFloorNumber) ||
                roomFloorNumber != floorNumber)
            {
                throw new BusinessException(
                    "BuildingManagement:RoomNumberDoesNotMatchFloor"
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
                    "BuildingManagement:RoomNumberAlreadyExists"
                )
                .WithData("FloorId", floorId)
                .WithData("RoomNumber", roomNumber);
            }
        }
    }
}
