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

        Task<List<RoomEquipmentDto>> GetEquipmentAsync(Guid id);

        Task<RoomEquipmentDto> CreateEquipmentAsync(
            Guid id, CreateRoomEquipmentDto input);

        Task<RoomEquipmentDto> UpdateEquipmentAsync(
            Guid id, Guid equipmentId, UpdateRoomEquipmentDto input);

        Task DeleteEquipmentAsync(Guid id, Guid equipmentId);

        Task<List<RoomPhotoDto>> GetPhotosAsync(Guid id);

        Task<RoomPhotoDto> UploadPhotoAsync(Guid id, UploadRoomPhotoDto input);

        Task<RoomPhotoFileDto> GetPhotoContentAsync(Guid id, Guid photoId);

        Task DeletePhotoAsync(Guid id, Guid photoId);

        Task<RoomDto> UpdateLayoutAsync(Guid id, UpdateRoomLayoutDto input);

        Task<RoomDto> UpdateRentAsync(Guid id, UpdateRoomRentDto input);
    }
}
