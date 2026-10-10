using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace BuildingManagement.RoomReservations
{
    public interface IRoomReservationAppService
        : IApplicationService
    {
        Task<RoomReservationDto> GetAsync(Guid id);

        Task<List<RoomReservationDto>> GetListAsync();

        Task<RoomReservationDto> CreateAsync(
            CreateRoomReservationDto input
        );

        Task<RoomReservationDto> CancelAsync(
            Guid id,
            CancelRoomReservationDto input
        );
    }
}