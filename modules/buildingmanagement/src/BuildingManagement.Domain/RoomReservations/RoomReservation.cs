using System;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.RoomReservations
{
    public class RoomReservation
        : FullAuditedAggregateRoot<Guid>
    {
        public string ReservationNumber { get; private set; } = default!;

        public Guid RoomId { get; private set; }

        public Guid TenantId { get; private set; }

        public DateTime ExpectedMoveInDate { get; private set; }

        public decimal QuotedMonthlyRent { get; private set; }

        public decimal RequiredDepositAmount { get; private set; }

        public decimal MinimumReservationDepositAmount
        {
            get;
            private set;
        }

        public RoomReservationStatus Status { get; private set; }

        public DateTime? ReservedAt { get; private set; }

        public DateTime? DepositDueDate { get; private set; }

        public Guid? ConvertedContractId { get; private set; }

        public DateTime? CancelledAt { get; private set; }

        public ReservationCancellationReason? CancellationReason
        {
            get;
            private set;
        }

        public string? CancellationNotes { get; private set; }

        public string? Notes { get; private set; }

        protected RoomReservation()
        {
        }

        public RoomReservation(
            Guid id,
            string reservationNumber,
            Guid roomId,
            Guid tenantId,
            DateTime expectedMoveInDate,
            decimal quotedMonthlyRent,
            decimal requiredDepositAmount,
            decimal minimumReservationDepositAmount,
            string? notes = null
        ) : base(id)
        {
            ReservationNumber =
                reservationNumber;

            RoomId =
                roomId;

            TenantId =
                tenantId;

            ExpectedMoveInDate =
                expectedMoveInDate.Date;

            QuotedMonthlyRent =
                quotedMonthlyRent;

            RequiredDepositAmount =
                requiredDepositAmount;

            MinimumReservationDepositAmount =
                minimumReservationDepositAmount;

            Status =
                RoomReservationStatus.PendingPayment;

            ReservedAt = null;

            DepositDueDate = null;

            ConvertedContractId = null;

            CancelledAt = null;

            CancellationReason = null;

            CancellationNotes = null;

            Notes =
                notes;
        }

        public void MarkReserved(
            DateTime reservedAt,
            DateTime depositDueDate)
        {
            ReservedAt =
                reservedAt;

            DepositDueDate =
                depositDueDate.Date;

            Status =
                RoomReservationStatus.Reserved;
        }

        public void ConvertToContract(
            Guid contractId)
        {
            ConvertedContractId =
                contractId;

            Status =
                RoomReservationStatus
                    .ConvertedToContract;
        }

        public void Cancel(
            DateTime cancelledAt,
            ReservationCancellationReason reason,
            string? cancellationNotes = null)
        {
            CancelledAt =
                cancelledAt;

            CancellationReason =
                reason;

            CancellationNotes =
                string.IsNullOrWhiteSpace(
                    cancellationNotes
                )
                    ? null
                    : cancellationNotes.Trim();

            Status =
                RoomReservationStatus.Cancelled;
        }
    }
}