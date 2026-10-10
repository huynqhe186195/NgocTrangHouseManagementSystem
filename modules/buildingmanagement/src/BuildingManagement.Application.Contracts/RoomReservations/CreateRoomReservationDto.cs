using System;
using System.ComponentModel.DataAnnotations;

namespace BuildingManagement.RoomReservations
{
    public class CreateRoomReservationDto
    {
        [Required]
        [MaxLength(64)]
        public string ReservationNumber { get; set; } = default!;

        public Guid RoomId { get; set; }

        public Guid TenantId { get; set; }

        public DateTime ExpectedMoveInDate { get; set; }

        public decimal QuotedMonthlyRent { get; set; }

        public decimal RequiredDepositAmount { get; set; }

        public decimal MinimumReservationDepositAmount
        {
            get;
            set;
        } = 500_000m;

        [MaxLength(500)]
        public string? Notes { get; set; }
    }
}