using System.ComponentModel.DataAnnotations;

namespace BuildingManagement.RoomReservations
{
    public class CancelRoomReservationDto
    {
        public ReservationCancellationReason Reason
        {
            get;
            set;
        }

        [MaxLength(500)]
        public string? Notes { get; set; }
    }
}