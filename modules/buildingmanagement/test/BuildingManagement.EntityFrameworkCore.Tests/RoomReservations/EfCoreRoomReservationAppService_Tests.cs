using BuildingManagement.RoomReservations;

namespace BuildingManagement.EntityFrameworkCore
    .Applications.RoomReservations;

public class EfCoreRoomReservationAppService_Tests
    : RoomReservationAppService_Tests<
        BuildingManagementEntityFrameworkCoreTestModule>
{
}