using System;
using Volo.Abp.Application.Dtos;

namespace BuildingManagement.RoomReservations
{
    public class RoomReservationDto
        : AuditedEntityDto<Guid>
    {
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
        }

        public RoomReservationStatus Status { get; set; }

        public DateTime? ReservedAt { get; set; }

        public DateTime? DepositDueDate { get; set; }

        public Guid? ConvertedContractId { get; set; }

        public DateTime? CancelledAt { get; set; }

        public ReservationCancellationReason?
            CancellationReason
        { get; set; }

        public string? CancellationNotes { get; set; }

        public string? Notes { get; set; }

    }
}