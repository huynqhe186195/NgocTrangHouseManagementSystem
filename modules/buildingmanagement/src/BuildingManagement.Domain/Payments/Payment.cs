using BuildingManagement.Payments.BuildingManagement.Payments;
using System;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.Payments
{
    public class Payment
        : FullAuditedAggregateRoot<Guid>
    {
        public string PaymentNumber { get; private set; } = default!;

        public Guid TenantId { get; private set; }

        public PaymentPurpose Purpose { get; private set; }

        public PaymentMethod Method { get; private set; }

        public PaymentChannel Channel { get; private set; }

        public PaymentStatus Status { get; private set; }

        public decimal Amount { get; private set; }

        public DateTime? PaidAt { get; private set; }

        public Guid? RoomReservationId { get; private set; }

        public Guid? ContractId { get; private set; }

        public Guid? InvoiceId { get; private set; }

        public DepositDisposition? DepositDisposition
        {
            get;
            private set;
        }

        public string? TransactionReference
        {
            get;
            private set;
        }

        public string? Notes { get; private set; }

        protected Payment()
        {
        }

        public Payment(
            Guid id,
            string paymentNumber,
            Guid tenantId,
            PaymentPurpose purpose,
            PaymentMethod method,
            PaymentChannel channel,
            decimal amount,
            Guid? roomReservationId = null,
            Guid? contractId = null,
            Guid? invoiceId = null,
            string? transactionReference = null,
            string? notes = null
        ) : base(id)
        {
            PaymentNumber =
                paymentNumber;

            TenantId =
                tenantId;

            Purpose =
                purpose;

            Method =
                method;

            Channel =
                channel;

            Amount =
                amount;

            RoomReservationId =
                roomReservationId;

            ContractId =
                contractId;

            InvoiceId =
                invoiceId;

            TransactionReference =
                NormalizeOptional(
                    transactionReference
                );

            Notes =
                NormalizeOptional(
                    notes
                );

            Status =
                PaymentStatus.Pending;

            PaidAt =
                null;

            DepositDisposition =
                null;
        }

        public void Complete(
            DateTime paidAt,
            string? transactionReference = null)
        {
            PaidAt =
                paidAt;

            if (!string.IsNullOrWhiteSpace(
                transactionReference))
            {
                TransactionReference =
                    transactionReference.Trim();
            }

            Status =
                PaymentStatus.Completed;

            if (
                Purpose ==
                    PaymentPurpose.ReservationDeposit
                ||
                Purpose ==
                    PaymentPurpose.ContractDeposit
            )
            {
                DepositDisposition =
                    Payments.DepositDisposition.Unapplied;
            }
        }

        public void Fail()
        {
            Status =
                PaymentStatus.Failed;
        }

        public void Cancel()
        {
            Status =
                PaymentStatus.Cancelled;
        }

        public void MarkDepositAsAppliedToContract()
        {
            DepositDisposition =
                Payments.DepositDisposition
                    .AppliedToContract;
        }

        public void MarkDepositAsRefunded()
        {
            DepositDisposition =
                Payments.DepositDisposition
                    .Refunded;
        }

        public void MarkDepositAsForfeited()
        {
            DepositDisposition =
                Payments.DepositDisposition
                    .Forfeited;
        }

        private static string? NormalizeOptional(
            string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim();
        }
    }
}