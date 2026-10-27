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
            EnsurePendingTransition("complete");
            if (paidAt == default)
                throw new ArgumentException("Completion timestamp is required.", nameof(paidAt));

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
            EnsurePendingTransition("fail");
            Status = PaymentStatus.Failed;
        }

        public void Cancel()
        {
            EnsurePendingTransition("cancel");
            Status = PaymentStatus.Cancelled;
        }

        private void EnsurePendingTransition(string transition)
        {
            // Completed financial receipts must never be silently rolled
            // back by a generic state transition. Corrections/refunds
            // require a separately audited, approved financial workflow.
            if (Status != PaymentStatus.Pending)
                throw new InvalidOperationException(
                    $"Cannot {transition} a payment in status {Status}.");
        }

        public void MarkDepositAsAppliedToContract()
        {
            EnsureUnappliedCompletedDeposit("apply");
            // Only a RESERVATION deposit may be allocated to its
            // Contract deposit. A Contract deposit is held until handover
            // and is not a second source of initial allocation.
            if (Purpose != PaymentPurpose.ReservationDeposit)
                throw new InvalidOperationException(
                    "Only reservation deposits can be applied to a contract.");
            DepositDisposition = Payments.DepositDisposition.AppliedToContract;
        }

        public void MarkDepositAsRefunded()
        {
            EnsureUnappliedCompletedDeposit("refund");
            DepositDisposition = Payments.DepositDisposition.Refunded;
        }

        public void MarkDepositAsForfeited()
        {
            EnsureUnappliedCompletedDeposit("forfeit");
            DepositDisposition = Payments.DepositDisposition.Forfeited;
        }

        private void EnsureUnappliedCompletedDeposit(string transition)
        {
            // This is only a domain invariant, NOT settlement authorization.
            // Calling services must additionally verify a unique audited
            // DepositSettlement, the owner's approval and the locked payment.
            if ((Purpose != PaymentPurpose.ReservationDeposit &&
                 Purpose != PaymentPurpose.ContractDeposit) ||
                Status != PaymentStatus.Completed ||
                DepositDisposition != Payments.DepositDisposition.Unapplied)
                throw new InvalidOperationException(
                    $"Cannot {transition} a deposit without a completed, unapplied deposit payment.");
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