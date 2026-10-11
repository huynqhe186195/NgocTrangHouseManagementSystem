using BuildingManagement.Payments.BuildingManagement.Payments;
using System;
using Volo.Abp.Application.Dtos;

namespace BuildingManagement.Payments
{
    public class PaymentDto
        : AuditedEntityDto<Guid>
    {
        public string PaymentNumber { get; set; } = default!;

        public Guid TenantId { get; set; }

        public PaymentPurpose Purpose { get; set; }

        public PaymentMethod Method { get; set; }

        public PaymentChannel Channel { get; set; }

        public PaymentStatus Status { get; set; }

        public decimal Amount { get; set; }

        public DateTime? PaidAt { get; set; }

        public Guid? RoomReservationId { get; set; }

        public Guid? ContractId { get; set; }

        public Guid? InvoiceId { get; set; }

        public DepositDisposition? DepositDisposition
        {
            get;
            set;
        }

        public string? TransactionReference
        {
            get;
            set;
        }

        public string? Notes { get; set; }
    }
}