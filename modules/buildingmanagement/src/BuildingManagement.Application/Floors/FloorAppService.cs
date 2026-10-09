using BuildingManagement.Buildings;
using BuildingManagement.Rooms;
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

        public FloorAppService(
            IRepository<Floor, Guid> floorRepository,
            IRepository<Building, Guid> buildingRepository,
            IRepository<Room, Guid> roomRepository)
        {
            _floorRepository = floorRepository;
            _buildingRepository = buildingRepository;
            _roomRepository = roomRepository;
        }

        public async Task<FloorDto> CreateAsync(
            CreateFloorDto input)
        {
            // Kiểm tra Building có tồn tại không
            await EnsureBuildingExistsAsync(
    input.BuildingId
);
            // Check exits floor
            await EnsureFloorNumberUniqueAsync(input.BuildingId, input.FloorNumber);

            var floor = new Floor(
                GuidGenerator.Create(),
                input.BuildingId,
                input.FloorNumber,
                input.Name,
                input.Description
            );

            await _floorRepository.InsertAsync(
                floor
            );

            return ObjectMapper.Map<
                Floor,
                FloorDto
            >(floor);
        }

        public async Task<List<FloorDto>> GetListAsync()
        {
            var floors =
                await _floorRepository.GetListAsync();

            return ObjectMapper.Map<
                List<Floor>,
                List<FloorDto>
            >(floors);
        }

        public async Task<FloorDto> GetAsync(
            Guid id)
        {
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
            var floor =
                await _floorRepository.GetAsync(id);

            await EnsureFloorNumberUniqueAsync(floor.BuildingId, input.FloorNumber, floor.Id);

            floor.Update(
                input.FloorNumber,
                input.Name,
                input.Description
            );

            await _floorRepository.UpdateAsync(
                floor
            );

            return ObjectMapper.Map<
                Floor,
                FloorDto
            >(floor);
        }

        public async Task DeleteAsync(Guid id)
        {
            await EnsureNoFloorRoomsAsync(id);
            await _floorRepository.DeleteAsync(
                id,
                autoSave: true
            );
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
