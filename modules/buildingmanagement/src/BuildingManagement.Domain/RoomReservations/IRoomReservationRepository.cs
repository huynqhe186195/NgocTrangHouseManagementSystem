using System;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace BuildingManagement.RoomReservations;

public interface IRoomReservationRepository
    : IRepository<RoomReservation, Guid>
{
    Task<RoomReservation?> FindForUpdateAsync(
        Guid id
    );
}