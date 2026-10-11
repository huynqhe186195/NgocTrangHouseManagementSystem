using BuildingManagement.Payments.BuildingManagement.Payments;
using System;
using System.ComponentModel.DataAnnotations;

namespace BuildingManagement.Payments
{
    public class CreateReservationDepositPaymentDto
    {
        [Required]
        [MaxLength(64)]
        public string PaymentNumber { get; set; } = default!;

        public Guid RoomReservationId { get; set; }

        public decimal Amount { get; set; }

        public PaymentMethod Method { get; set; }

        public PaymentChannel Channel { get; set; }

        [MaxLength(128)]
        public string? TransactionReference
        {
            get;
            set;
        }

        [MaxLength(500)]
        public string? Notes { get; set; }
    }
}