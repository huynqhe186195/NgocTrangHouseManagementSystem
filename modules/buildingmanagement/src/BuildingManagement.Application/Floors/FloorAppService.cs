using BuildingManagement.Buildings;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
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

        public FloorAppService(
            IRepository<Floor, Guid> floorRepository,
            IRepository<Building, Guid> buildingRepository)
        {
            _floorRepository = floorRepository;
            _buildingRepository = buildingRepository;
        }

        public async Task<FloorDto> CreateAsync(
            CreateFloorDto input)
        {
            // Kiểm tra Building có tồn tại không
            await _buildingRepository.GetAsync(
                input.BuildingId
            );

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
            await _floorRepository.DeleteAsync(
                id,
                autoSave: true
            );
        }
    }
}
