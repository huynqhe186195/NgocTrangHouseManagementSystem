using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.RoomReservations;
using BuildingManagement.Rooms;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Timing;

namespace BuildingManagement.Payments
{
    public class PaymentAppService
        : ApplicationService,
          IPaymentAppService
    {
        private readonly IRepository<Payment, Guid>
            _paymentRepository;

        private readonly IRepository<RoomReservation, Guid>
            _roomReservationRepository;

        private readonly IRepository<ContractRenewalHold, Guid>
            _renewalHoldRepository;

        private readonly IRepository<Room, Guid>
            _roomRepository;

        private readonly IDataFilter<ISoftDelete>
            _softDeleteFilter;

        private readonly IClock
            _clock;

        private readonly IRepository<Contract, Guid>
    _contractRepository;

        private readonly IRepository<ContractTenant, Guid>
            _contractTenantRepository;

        private readonly IRepository<DepositSettlement, Guid>
            _depositSettlementRepository;

        private readonly IContractRepository
    _contractLockRepository;

        public PaymentAppService(
            IRepository<Payment, Guid> paymentRepository,
            IRepository<RoomReservation, Guid>
                roomReservationRepository,
            IRepository<ContractRenewalHold, Guid>
                renewalHoldRepository,
            IRepository<Room, Guid> roomRepository,
            IDataFilter<ISoftDelete> softDeleteFilter,
            IClock clock,
            IRepository<Contract, Guid> contractRepository,
            IRepository<ContractTenant, Guid> contractTenantRepository,
            IRepository<DepositSettlement, Guid> depositSettlementRepository,
            IContractRepository contractLockRepository)
        {
            _paymentRepository =
                paymentRepository;

            _roomReservationRepository =
                roomReservationRepository;

            _renewalHoldRepository =
                renewalHoldRepository;

            _roomRepository =
                roomRepository;

            _softDeleteFilter =
                softDeleteFilter;

            _clock =
                clock;

            _contractRepository = contractRepository;

            _contractTenantRepository = contractTenantRepository;

            _depositSettlementRepository = depositSettlementRepository;
            _contractLockRepository = contractLockRepository;
        }

        private async Task<decimal> GetCoveredContractDepositTotalAsync(
        Guid contractId)
        {
            var queryable =
                await _depositSettlementRepository
                    .GetQueryableAsync();

            var settlements =
                await AsyncExecuter.ToListAsync(
                    queryable.Where(
                        x =>
                            x.TargetContractId ==
                                contractId
                            &&
                            x.Status ==
                                DepositSettlementStatus.Completed
                            &&
                            (
                                x.Type ==
                                    DepositSettlementType
                                        .AllocateToContractDeposit
                                ||
                                x.Type ==
                                    DepositSettlementType
                                        .ContractTransfer
                                ||
                                x.Type ==
                                    DepositSettlementType
                                        .RoomChangeCarryForward
                            )
                    )
                );

            return settlements.Sum(
                x => x.Amount
            );
        }

        private static void EnsureContractDepositIsPayable(
        Contract contract)
        {
            if (
                contract.Status !=
                    ContractStatus.Signed
                &&
                contract.Status !=
                    ContractStatus.Active
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractDepositNotPayable
                );
            }
        }

        private async Task<Contract> GetExistingContractAsync(Guid id)
        {
            var contract =
                await _contractRepository.FindAsync(
                    id
                );

            if (contract is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractNotFound
                );
            }

            return contract;
        }

        public async Task<PaymentDto> CreateContractDepositAsync(
        CreateContractDepositPaymentDto input)
        {
            var paymentNumber =
                NormalizeCode(
                    input.PaymentNumber
                );

            if (string.IsNullOrWhiteSpace(
                paymentNumber))
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidPaymentNumber
                );
            }

            if (input.Amount <= 0)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidPaymentAmount
                );
            }

            ValidatePaymentMethodAndChannel(
                input.Method,
                input.Channel
            );

            var contract =
                await GetExistingContractAsync(
                    input.ContractId
                );

            EnsureContractDepositIsPayable(
                contract
            );

            var primaryTenant =
                await _contractTenantRepository
                    .FindAsync(
                        x =>
                            x.ContractId ==
                                contract.Id
                            &&
                            x.Role ==
                                ContractTenantRole
                                    .PrimaryTenant
                    );

            if (primaryTenant is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractRequiresPrimaryTenant
                );
            }

            await EnsurePaymentNumberIsUniqueAsync(
                paymentNumber
            );

            var transactionReference =
                NormalizeOptional(
                    input.TransactionReference
                );

            if (transactionReference is not null)
            {
                await EnsureTransactionReferenceIsUniqueAsync(
                    transactionReference
                );
            }

            var coveredDeposit =
                await GetCoveredContractDepositTotalAsync(
                contract.Id
            );

            var remaining =
                contract.DepositAmount
                - coveredDeposit;

            if (remaining <= 0)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractDepositAlreadyFullyPaid
                );
            }

            if (input.Amount > remaining)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractDepositExceedsRemaining
                );
            }

            var payment =
                new Payment(
                    GuidGenerator.Create(),
                    paymentNumber,
                    primaryTenant.TenantId,
                    PaymentPurpose.ContractDeposit,
                    input.Method,
                    input.Channel,
                    input.Amount,
                    contractId: contract.Id,
                    transactionReference:
                        transactionReference,
                    notes:
                        NormalizeOptional(
                            input.Notes
                        )
                );

            await _paymentRepository.InsertAsync(
                payment,
                autoSave: true
            );

            return ObjectMapper.Map<
                Payment,
                PaymentDto
            >(payment);
        }

        public async Task<PaymentDto>
            GetAsync(Guid id)
        {
            var payment =
                await GetExistingPaymentAsync(id);

            return ObjectMapper.Map<
                Payment,
                PaymentDto
            >(payment);
        }

        public async Task<List<PaymentDto>>
            GetListAsync()
        {
            var queryable =
                await _paymentRepository
                    .GetQueryableAsync();

            var payments =
                await AsyncExecuter.ToListAsync(
                    queryable.OrderByDescending(
                        x => x.CreationTime
                    )
                );

            return ObjectMapper.Map<
                List<Payment>,
                List<PaymentDto>
            >(payments);
        }

        public async Task<PaymentDto>
            CreateReservationDepositAsync(
                CreateReservationDepositPaymentDto input)
        {
            var paymentNumber =
                NormalizeCode(
                    input.PaymentNumber
                );

            if (string.IsNullOrWhiteSpace(
                paymentNumber))
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidPaymentNumber
                );
            }

            if (input.Amount <= 0)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidPaymentAmount
                );
            }

            ValidatePaymentMethodAndChannel(
                input.Method,
                input.Channel
            );

            var reservation =
                await GetExistingReservationAsync(
                    input.RoomReservationId
                );

            EnsureReservationIsPayable(
                reservation
            );

            if (
                reservation.Status ==
                    RoomReservationStatus.PendingPayment
            )
            {
                await EnsureNoOtherReservedReservationAsync(
                    reservation
                );
            }

            await EnsurePaymentNumberIsUniqueAsync(
                paymentNumber
            );

            var transactionReference =
                NormalizeOptional(
                    input.TransactionReference
                );

            if (transactionReference is not null)
            {
                await EnsureTransactionReferenceIsUniqueAsync(
                    transactionReference
                );
            }

            var totalPaid =
                await GetCompletedReservationDepositTotalAsync(
                    reservation.Id
                );

            var remaining =
                reservation.RequiredDepositAmount
                - totalPaid;

            if (remaining <= 0)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ReservationDepositAlreadyFullyPaid
                );
            }

            if (input.Amount > remaining)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ReservationDepositExceedsRemaining
                );
            }

            var payment =
                new Payment(
                    GuidGenerator.Create(),
                    paymentNumber,
                    reservation.TenantId,
                    PaymentPurpose
                        .ReservationDeposit,
                    input.Method,
                    input.Channel,
                    input.Amount,
                    roomReservationId:
                        reservation.Id,
                    transactionReference:
                        transactionReference,
                    notes:
                        NormalizeOptional(
                            input.Notes
                        )
                );

            await _paymentRepository.InsertAsync(
                payment,
                autoSave: true
            );

            return ObjectMapper.Map<
                Payment,
                PaymentDto
            >(payment);
        }

        public async Task<PaymentDto>CompleteAsync(
                Guid id,
                CompletePaymentDto input)
        {
            var payment =
                await GetExistingPaymentAsync(id);

            if (payment.Status !=
                PaymentStatus.Pending)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .PaymentCannotBeCompleted
                );
            }

            if (payment.Purpose == PaymentPurpose.ContractDeposit)
            {
                return await CompleteContractDepositAsync(
                        payment,
                        input
                    );
            }

            if (
                payment.Purpose !=
                    PaymentPurpose.ReservationDeposit
                ||
                !payment.RoomReservationId.HasValue
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .PaymentCannotBeCompleted
                );
            }

            var reservation =
                await GetExistingReservationAsync(
                    payment.RoomReservationId.Value
                );

            EnsureReservationIsPayable(
                reservation
            );

            if (
                reservation.TenantId !=
                    payment.TenantId
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .PaymentCannotBeCompleted
                );
            }

            if (
                reservation.Status ==
                    RoomReservationStatus.PendingPayment
            )
            {
                await EnsureNoOtherReservedReservationAsync(
                    reservation
                );

                await EnsureNoActiveRenewalHoldAsync(
                    reservation.RoomId
                );
            }

            var totalPaid =
                await GetCompletedReservationDepositTotalAsync(
                    reservation.Id
                );

            var remaining =
                reservation.RequiredDepositAmount
                - totalPaid;

            if (remaining <= 0)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ReservationDepositAlreadyFullyPaid
                );
            }

            if (payment.Amount > remaining)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ReservationDepositExceedsRemaining
                );
            }

            if (
                reservation.Status ==
                    RoomReservationStatus.PendingPayment
                &&
                totalPaid == 0
                &&
                payment.Amount <
                    reservation
                        .MinimumReservationDepositAmount
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ReservationDepositBelowMinimum
                );
            }

            var inputTransactionReference =
                NormalizeOptional(
                    input.TransactionReference
                );

            var effectiveTransactionReference =
                inputTransactionReference
                ?? payment.TransactionReference;

            if (
                payment.Method !=
                    PaymentMethod.Cash
                &&
                string.IsNullOrWhiteSpace(
                    effectiveTransactionReference
                )
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .TransactionReferenceRequired
                );
            }

            if (effectiveTransactionReference is not null)
            {
                await EnsureTransactionReferenceIsUniqueAsync(
                    effectiveTransactionReference,
                    payment.Id
                );
            }

            var paidAt =
                _clock.Now;

            payment.Complete(
                paidAt,
                inputTransactionReference
            );

            await _paymentRepository.UpdateAsync(
                payment,
                autoSave: true
            );

            if (
                reservation.Status ==
                    RoomReservationStatus.PendingPayment
            )
            {
                reservation.MarkReserved(
                    paidAt,
                    GetEndOfMonth(
                        paidAt
                    )
                );

                await _roomReservationRepository
                    .UpdateAsync(
                        reservation,
                        autoSave: true
                    );

                var room =
                    await _roomRepository.FindAsync(
                        reservation.RoomId
                    );

                if (room is null)
                {
                    throw new BusinessException(
                        BuildingManagementErrorCodes
                            .RoomNotFound
                    );
                }

                if (
                    room.Status ==
                        RoomStatus.Available
                )
                {
                    room.ChangeStatus(
                        RoomStatus.Reserved
                    );

                    await _roomRepository.UpdateAsync(
                        room,
                        autoSave: true
                    );
                }
            }

            return ObjectMapper.Map<
                Payment,
                PaymentDto
            >(payment);
        }

        private async Task<PaymentDto>
    CompleteContractDepositAsync(
        Payment payment,
        CompletePaymentDto input)
        {
            if (!payment.ContractId.HasValue)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .PaymentCannotBeCompleted
                );
            }

            var contract = await _contractLockRepository.FindForUpdateAsync(payment.ContractId.Value);

            if (contract is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractNotFound
                );
            }

            EnsureContractDepositIsPayable(
                contract
            );

            var primaryTenant =
                await _contractTenantRepository
                    .FindAsync(
                        x =>
                            x.ContractId ==
                                contract.Id
                            &&
                            x.Role ==
                                ContractTenantRole
                                    .PrimaryTenant
                    );

            if (
                primaryTenant is null
                ||
                primaryTenant.TenantId !=
                    payment.TenantId
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .PaymentCannotBeCompleted
                );
            }

            var coveredDeposit =
                await GetCoveredContractDepositTotalAsync(
                    contract.Id
                );

            var remaining =
                contract.DepositAmount
                - coveredDeposit;

            if (remaining <= 0)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractDepositAlreadyFullyPaid
                );
            }

            if (payment.Amount > remaining)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractDepositExceedsRemaining
                );
            }

            var inputTransactionReference =
                NormalizeOptional(
                    input.TransactionReference
                );

            var effectiveTransactionReference =
                inputTransactionReference
                ?? payment.TransactionReference;

            if (
                payment.Method !=
                    PaymentMethod.Cash
                &&
                string.IsNullOrWhiteSpace(
                    effectiveTransactionReference
                )
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .TransactionReferenceRequired
                );
            }

            if (
                effectiveTransactionReference
                is not null
            )
            {
                await EnsureTransactionReferenceIsUniqueAsync(
                    effectiveTransactionReference,
                    payment.Id
                );
            }

            var paidAt =
                _clock.Now;

            payment.Complete(
                paidAt,
                inputTransactionReference
            );

            await _paymentRepository.UpdateAsync(
                payment,
                autoSave: true
            );

            var settlement =
                new DepositSettlement(
                    GuidGenerator.Create(),
                    DepositSettlementType
                        .AllocateToContractDeposit,
                    payment.Amount,
                    paymentId:
                        payment.Id,
                    targetContractId:
                        contract.Id,
                    notes:
                        $"Contract deposit payment {payment.PaymentNumber}"
                );

            settlement.Complete(
                paidAt
            );

            await _depositSettlementRepository
                .InsertAsync(
                    settlement,
                    autoSave: true
                );

            return ObjectMapper.Map<
                Payment,
                PaymentDto
            >(payment);
        }

        public async Task<PaymentDto>
            CancelAsync(Guid id)
        {
            var payment =
                await GetExistingPaymentAsync(id);

            if (payment.Status !=
                PaymentStatus.Pending)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .PaymentCannotBeCancelled
                );
            }

            payment.Cancel();

            await _paymentRepository.UpdateAsync(
                payment,
                autoSave: true
            );

            return ObjectMapper.Map<
                Payment,
                PaymentDto
            >(payment);
        }

        private async Task<Payment>
            GetExistingPaymentAsync(Guid id)
        {
            var payment =
                await _paymentRepository.FindAsync(
                    id
                );

            if (payment is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .PaymentNotFound
                );
            }

            return payment;
        }

        private async Task<RoomReservation>
            GetExistingReservationAsync(Guid id)
        {
            var reservation =
                await _roomReservationRepository
                    .FindAsync(id);

            if (reservation is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .RoomReservationNotFound
                );
            }

            return reservation;
        }

        private static void
            EnsureReservationIsPayable(
                RoomReservation reservation)
        {
            if (
                reservation.Status !=
                    RoomReservationStatus.PendingPayment
                &&
                reservation.Status !=
                    RoomReservationStatus.Reserved
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .RoomReservationNotPayable
                );
            }
        }

        private async Task
            EnsureNoOtherReservedReservationAsync(
                RoomReservation reservation)
        {
            var exists =
                await _roomReservationRepository
                    .AnyAsync(
                        x =>
                            x.RoomId ==
                                reservation.RoomId
                            &&
                            x.Id !=
                                reservation.Id
                            &&
                            x.Status ==
                                RoomReservationStatus
                                    .Reserved
                    );

            if (exists)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .RoomAlreadyReserved
                );
            }
        }

        private async Task
            EnsureNoActiveRenewalHoldAsync(
                Guid roomId)
        {
            var now =
                _clock.Now;

            var exists =
                await _renewalHoldRepository
                    .AnyAsync(
                        x =>
                            x.RoomId == roomId
                            &&
                            x.Status ==
                                ContractRenewalHoldStatus
                                    .Active
                            &&
                            x.ExpiresAt > now
                    );

            if (exists)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ActiveRenewalHoldBlocksReservationDeposit
                );
            }
        }

        private async Task<decimal>
            GetCompletedReservationDepositTotalAsync(
                Guid reservationId)
        {
            var queryable =
                await _paymentRepository
                    .GetQueryableAsync();

            var completedPayments =
                await AsyncExecuter.ToListAsync(
                    queryable.Where(
                        x =>
                            x.RoomReservationId ==
                                reservationId
                            &&
                            x.Purpose ==
                                PaymentPurpose
                                    .ReservationDeposit
                            &&
                            x.Status ==
                                PaymentStatus.Completed
                    )
                );

            return completedPayments.Sum(
                x => x.Amount
            );
        }

        private async Task
            EnsurePaymentNumberIsUniqueAsync(
                string paymentNumber)
        {
            bool exists;

            using (_softDeleteFilter.Disable())
            {
                exists =
                    await _paymentRepository.AnyAsync(
                        x =>
                            x.PaymentNumber ==
                                paymentNumber
                    );
            }

            if (exists)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .PaymentNumberAlreadyExists
                );
            }
        }

        private async Task
            EnsureTransactionReferenceIsUniqueAsync(
                string transactionReference,
                Guid? excludedPaymentId = null)
        {
            bool exists;

            using (_softDeleteFilter.Disable())
            {
                exists =
                    await _paymentRepository.AnyAsync(
                        x =>
                            x.TransactionReference ==
                                transactionReference
                            &&
                            (
                                !excludedPaymentId.HasValue
                                ||
                                x.Id !=
                                    excludedPaymentId.Value
                            )
                    );
            }

            if (exists)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .TransactionReferenceAlreadyExists
                );
            }
        }

        private static void
            ValidatePaymentMethodAndChannel(
                PaymentMethod method,
                PaymentChannel channel)
        {
            if (!Enum.IsDefined(
                typeof(PaymentMethod),
                method))
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidPaymentMethod
                );
            }

            if (!Enum.IsDefined(
                typeof(PaymentChannel),
                channel))
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidPaymentChannel
                );
            }

            var isValid =
                method switch
                {
                    PaymentMethod.Cash =>
                        channel ==
                            PaymentChannel.Manual,

                    PaymentMethod.BankTransfer =>
                        channel ==
                            PaymentChannel.Manual
                        ||
                        channel ==
                            PaymentChannel.VietQr,

                    PaymentMethod.EWallet =>
                        channel ==
                            PaymentChannel.Momo
                        ||
                        channel ==
                            PaymentChannel.ZaloPay,

                    PaymentMethod.Card =>
                        channel ==
                            PaymentChannel.VnPay,

                    _ => false
                };

            if (!isValid)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidPaymentMethodChannel
                );
            }
        }

        private static DateTime
            GetEndOfMonth(DateTime date)
        {
            return new DateTime(
                date.Year,
                date.Month,
                DateTime.DaysInMonth(
                    date.Year,
                    date.Month
                )
            );
        }

        private static string
            NormalizeCode(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value
                    .Trim()
                    .ToUpperInvariant();
        }

        private static string?
            NormalizeOptional(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim();
        }
    }
}