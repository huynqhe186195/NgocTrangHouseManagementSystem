using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace BuildingManagement.Rooms
{
    public interface IRoomAppService : IApplicationService
    {
        Task<RoomDto> CreateAsync(CreateRoomDto input);

        Task<List<RoomDto>> GetListAsync();

        Task<RoomDto> GetAsync(Guid id);

        Task<RoomDto> UpdateAsync(
            Guid id,
            UpdateRoomDto input
        );

        Task DeleteAsync(Guid id);
    }
}
