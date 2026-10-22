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
using Volo.Abp.Uow;

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

        private readonly IRoomReservationRepository
            _roomReservationLockRepository;

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
            IContractRepository contractLockRepository, 
            IRoomReservationRepository roomReservationLockRepository)
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

            _clock = clock;

            _contractRepository = contractRepository;

            _contractTenantRepository = contractTenantRepository;

            _depositSettlementRepository = depositSettlementRepository;

            _contractLockRepository = contractLockRepository;

            _roomReservationLockRepository = roomReservationLockRepository;
        }

        private async Task<decimal> GetCoveredContractDepositTotalAsync(Guid contractId)
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

        private static void EnsureContractDepositIsPayable(Contract contract)
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

        public async Task<PaymentDto> CreateContractDepositAsync(CreateContractDepositPaymentDto input)
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

        // Contract must be locked before Reservation in every payment flow.
        // Both locks must remain held in one database transaction.
        [UnitOfWork(isTransactional: true)]
        public virtual async Task<ContractDepositAllocationDto> AllocateReservationDepositToContractAsync(
            Guid reservationId,
            Guid contractId)
        {
            /*
             * Shared lock order: Contract -> Reservation.
             *
             * ReservationDeposit.CompleteAsync uses the same order when an
             * Active Contract exists. Reversing the order can deadlock with
             * an in-flight top-up payment for the same reservation.
             *
             * Locking the target Contract first also serializes all other
             * deposit coverage/ContractDeposit completion for this Contract.
             */
            var contract =
                await _contractLockRepository
                    .FindForUpdateAsync(
                        contractId
                    );

            if (contract is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractNotFound
                );
            }

            /*
             * Lock the source only after acquiring the Contract lock.
             * This still prevents duplicate allocation from a Reservation,
             * including two concurrent requests targeting different contracts.
             */
            var reservation =
                await _roomReservationLockRepository
                    .FindForUpdateAsync(
                        reservationId
                    );

            if (reservation is null)
            {
                await GetExistingReservationAsync(
                    reservationId
                );

                throw new InvalidOperationException(
                    "Unable to lock the room reservation."
                );
            }

            /*
             * Reservation deposit chỉ được chuyển
             * sang ContractDeposit khi khách thực sự
             * đã vào ở => Contract phải Active.
             */
            if (contract.Status !=
                ContractStatus.Active)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ReservationDepositCannotBeAllocated
                );
            }

            /*
             * Reservation phải đang giữ phòng,
             * hoặc đã được convert đúng sang Contract này.
             */
            if (
                reservation.Status !=
                    RoomReservationStatus.Reserved
                &&
                reservation.Status !=
                    RoomReservationStatus.ConvertedToContract
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ReservationDepositCannotBeAllocated
                );
            }

            /*
             * Không được lấy tiền giữ phòng A
             * sang Contract của phòng B.
             */
            if (reservation.RoomId !=
                contract.RoomId)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ReservationContractMismatch
                );
            }

            /*
             * Nếu Reservation đã convert rồi
             * thì bắt buộc phải convert đúng Contract này.
             */
            if (
                reservation.Status ==
                    RoomReservationStatus.ConvertedToContract
                &&
                reservation.ConvertedContractId !=
                    contract.Id
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ReservationContractMismatch
                );
            }

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

            /*
             * Người đặt phòng phải chính là
             * PrimaryTenant của Contract.
             */
            if (primaryTenant.TenantId !=
                reservation.TenantId)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ReservationContractMismatch
                );
            }

            /*
             * Lấy toàn bộ Payment ReservationDeposit
             * đã thực sự Completed.
             */
            var paymentQueryable =
                await _paymentRepository
                    .GetQueryableAsync();

            var completedPayments =
                await AsyncExecuter.ToListAsync(
                    paymentQueryable
                        .Where(
                            x =>
                                x.RoomReservationId ==
                                    reservation.Id
                                &&
                                x.Purpose ==
                                    PaymentPurpose
                                        .ReservationDeposit
                                &&
                                x.Status ==
                                    PaymentStatus.Completed
                        )
                        .OrderBy(x => x.PaidAt)
                        .ThenBy(x => x.CreationTime)
                );

            if (completedPayments.Count == 0)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ReservationDepositCannotBeAllocated
                );
            }

            /*
             * Lấy settlement đã tồn tại cho
             * Reservation này.
             */
            var settlementQueryable =
                await _depositSettlementRepository
                    .GetQueryableAsync();

            var existingAllocations =
                await AsyncExecuter.ToListAsync(
                    settlementQueryable.Where(
                        x =>
                            x.Type ==
                                DepositSettlementType
                                    .AllocateToContractDeposit
                            &&
                            x.Status ==
                                DepositSettlementStatus
                                    .Completed
                            &&
                            x.SourceRoomReservationId ==
                                reservation.Id
                    )
                );

            /*
             * Một Reservation không được phân bổ
             * sang hai Contract khác nhau.
             */
            if (
                existingAllocations.Any(
                    x =>
                        x.TargetContractId !=
                            contract.Id
                )
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ReservationContractMismatch
                );
            }

            /*
             * Payment có thể đã được allocate một phần
             * ở lần xử lý trước.
             *
             * Không được allocate lại cùng một đồng tiền.
             */
            var allocatedByPayment =
                existingAllocations
                    .Where(x => x.PaymentId.HasValue)
                    .GroupBy(x => x.PaymentId!.Value)
                    .ToDictionary(
                        x => x.Key,
                        x => x.Sum(y => y.Amount)
                    );

            var coveredDeposit =
                await GetCoveredContractDepositTotalAsync(
                    contract.Id
                );

            var remainingDeposit =
                contract.DepositAmount
                - coveredDeposit;

            decimal allocatedNow = 0;

            foreach (var payment in completedPayments)
            {
                var alreadyAllocated =
                    allocatedByPayment.TryGetValue(
                        payment.Id,
                        out var value
                    )
                        ? value
                        : 0m;

                var availableAmount =
                    payment.Amount
                    - alreadyAllocated;

                if (availableAmount <= 0)
                {
                    continue;
                }

                /*
                 * Contract đã đủ cọc nhưng Reservation
                 * vẫn còn tiền chưa disposition.
                 *
                 * Không tự ý refund/rent-credit ở đây.
                 */
                if (remainingDeposit <= 0)
                {
                    break;
                }

                var amountToAllocate =
                    Math.Min(
                        availableAmount,
                        remainingDeposit
                    );

                var settlement =
                    new DepositSettlement(
                        GuidGenerator.Create(),
                        DepositSettlementType
                            .AllocateToContractDeposit,
                        amountToAllocate,
                        paymentId:
                            payment.Id,
                        sourceRoomReservationId:
                            reservation.Id,
                        targetContractId:
                            contract.Id,
                        notes:
                            $"Reservation deposit allocation from {reservation.ReservationNumber}"
                    );

                settlement.Complete(
                    _clock.Now
                );

                await _depositSettlementRepository
                    .InsertAsync(
                        settlement,
                        autoSave: true
                    );

                allocatedNow +=
                    amountToAllocate;

                remainingDeposit -=
                    amountToAllocate;

                if (remainingDeposit <= 0)
                {
                    break;
                }
            }

            /*
             * Nếu không allocate được thêm đồng nào:
             *
             * - Nếu Reservation đã được allocation trước
             *   vào đúng Contract này => operation idempotent,
             *   trả kết quả hiện tại.
             *
             * - Nếu Contract đã đủ bởi nguồn khác nhưng
             *   Reservation còn tiền chưa xử lý => không tự
             *   quyết định disposition.
             */
            if (allocatedNow == 0)
            {
                var totalReservationPaid =
                    completedPayments.Sum(
                        x => x.Amount
                    );

                var totalReservationAllocated =
                    existingAllocations.Sum(
                        x => x.Amount
                    );

                if (
                    totalReservationAllocated <
                        totalReservationPaid
                    &&
                    remainingDeposit <= 0
                )
                {
                    throw new BusinessException(
                        BuildingManagementErrorCodes
                            .ContractDepositAlreadyFullyPaid
                    );
                }
            }

            /*
             * Đây chính là Reservation đã tạo ra
             * Contract mà khách đang ở.
             */
            if (
                reservation.Status ==
                    RoomReservationStatus.Reserved
            )
            {
                reservation.ConvertToContract(
                    contract.Id
                );

                await _roomReservationRepository
                    .UpdateAsync(
                        reservation,
                        autoSave: true
                    );
            }

            var coveredAfter =
                coveredDeposit
                + allocatedNow;

            var remainingAfter =
                Math.Max(
                    contract.DepositAmount
                    - coveredAfter,
                    0m
                );

            return new ContractDepositAllocationDto
            {
                RoomReservationId =
                    reservation.Id,

                ContractId =
                    contract.Id,

                AllocatedAmount =
                    allocatedNow,

                CoveredDeposit =
                    coveredAfter,

                RemainingDeposit =
                    remainingAfter
            };
        }

        public async Task<ReservationDepositSettlementDto> SettleCancelledReservationDepositAsync(
            Guid reservationId)
        {
            /*
             * Lock Reservation để:
             * - không settlement song song 2 lần
             * - không vừa allocate sang Contract
             *   vừa refund/forfeit
             */
            var reservation =
                await _roomReservationLockRepository
                    .FindForUpdateAsync(
                        reservationId
                    );

            if (reservation is null)
            {
                await GetExistingReservationAsync(
                    reservationId
                );

                throw new InvalidOperationException(
                    "Unable to lock the room reservation."
                );
            }

            /*
             * Chỉ settlement sau khi Reservation
             * thực sự đã bị cancel.
             */
            if (
                reservation.Status !=
                    RoomReservationStatus.Cancelled
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ReservationDepositCannotBeSettled
                );
            }

            /*
             * Chỉ áp dụng policy khách hủy/no-show.
             *
             * SupersededByRenewal là một business
             * event khác, không được tự forfeit tiền.
             */
            if (
                reservation.CancellationReason !=
                    ReservationCancellationReason
                        .CustomerCancelled
                &&
                reservation.CancellationReason !=
                    ReservationCancellationReason
                        .NoShow
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ReservationDepositCannotBeSettled
                );
            }

            var paymentQueryable =
                await _paymentRepository
                    .GetQueryableAsync();

            var completedPayments =
                await AsyncExecuter.ToListAsync(
                    paymentQueryable
                        .Where(
                            x =>
                                x.RoomReservationId ==
                                    reservation.Id
                                &&
                                x.Purpose ==
                                    PaymentPurpose
                                        .ReservationDeposit
                                &&
                                x.Status ==
                                    PaymentStatus.Completed
                        )
                        .OrderBy(x => x.PaidAt)
                        .ThenBy(x => x.CreationTime)
                );

            if (completedPayments.Count == 0)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ReservationDepositCannotBeSettled
                );
            }

            var totalPaid =
                completedPayments.Sum(
                    x => x.Amount
                );

            var settlementQueryable =
                await _depositSettlementRepository
                    .GetQueryableAsync();

            var existingSettlements =
                await AsyncExecuter.ToListAsync(
                    settlementQueryable.Where(
                        x =>
                            x.SourceRoomReservationId ==
                                reservation.Id
                            &&
                            x.Status ==
                                DepositSettlementStatus
                                    .Completed
                    )
                );

            /*
             * Tiền đã được chuyển sang ContractDeposit
             * thì tuyệt đối không được refund/forfeit
             * lại lần nữa.
             */
            if (
                existingSettlements.Any(
                    x =>
                        x.Type ==
                            DepositSettlementType
                                .AllocateToContractDeposit
                )
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ReservationDepositAlreadyAllocated
                );
            }

            DepositSettlementType dispositionType;

            /*
             * Check FULL DEPOSIT trước.
             *
             * Khách đã đóng đủ 1 tháng trước move-in,
             * nhưng không vào ở => refund 100%.
             */
            if (
                totalPaid ==
                reservation.RequiredDepositAmount
            )
            {
                dispositionType =
                    DepositSettlementType.Refund;
            }
            /*
             * Standard hold 500k:
             * khách đổi ý/no-show => mất cọc.
             */
            else if (
                totalPaid ==
                reservation.MinimumReservationDepositAmount
            )
            {
                dispositionType =
                    DepositSettlementType.Forfeit;
            }
            else
            {
                /*
                 * Ví dụ:
                 * Required = 3m
                 * Paid = 1.5m
                 *
                 * Business chưa chốt => không tự xử lý.
                 */
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ReservationDepositDispositionRequiresReview
                );
            }

            /*
             * Nếu operation đã chạy một phần hoặc
             * được gọi lại, disposition phải nhất quán.
             */
            var previousDispositionSettlements =
                existingSettlements
                    .Where(
                        x =>
                            x.Type ==
                                DepositSettlementType.Refund
                            ||
                            x.Type ==
                                DepositSettlementType.Forfeit
                    )
                    .ToList();

            if (
                previousDispositionSettlements.Any(
                    x =>
                        x.Type != dispositionType
                )
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ReservationDepositCannotBeSettled
                );
            }

            var settledByPayment =
                previousDispositionSettlements
                    .Where(x => x.PaymentId.HasValue)
                    .GroupBy(
                        x => x.PaymentId!.Value
                    )
                    .ToDictionary(
                        x => x.Key,
                        x => x.Sum(y => y.Amount)
                    );

            decimal settledNow = 0;

            foreach (var payment in completedPayments)
            {
                var alreadySettled =
                    settledByPayment.TryGetValue(
                        payment.Id,
                        out var value
                    )
                        ? value
                        : 0m;

                var amountToSettle =
                    payment.Amount
                    - alreadySettled;

                if (amountToSettle <= 0)
                {
                    continue;
                }

                var settlement =
                    new DepositSettlement(
                        GuidGenerator.Create(),
                        dispositionType,
                        amountToSettle,
                        paymentId:
                            payment.Id,
                        sourceRoomReservationId:
                            reservation.Id,
                        notes:
                            dispositionType ==
                                DepositSettlementType.Refund
                                ? $"Full reservation deposit refund for {reservation.ReservationNumber}"
                                : $"Reservation hold forfeiture for {reservation.ReservationNumber}"
                    );

                settlement.Complete(
                    _clock.Now
                );

                await _depositSettlementRepository
                    .InsertAsync(
                        settlement,
                        autoSave: true
                    );

                settledNow +=
                    amountToSettle;
            }

            var previousSettled =
                previousDispositionSettlements.Sum(
                    x => x.Amount
                );

            return new ReservationDepositSettlementDto
            {
                RoomReservationId =
                    reservation.Id,

                DispositionType =
                    dispositionType,

                TotalPaid =
                    totalPaid,

                SettledNow =
                    settledNow,

                TotalSettled =
                    previousSettled
                    + settledNow
            };
        }

        private static void ValidateReservationDepositPaymentAmount(RoomReservation reservation,
            decimal completedAmount,
            decimal paymentAmount)
        {
            var minimum =
                reservation
                    .MinimumReservationDepositAmount;

            var required =
                reservation
                    .RequiredDepositAmount;

            /*
             * Chưa đóng tiền:
             * chỉ được 500k hoặc full deposit.
             */
            if (completedAmount == 0)
            {
                if (
                    paymentAmount != minimum
                    &&
                    paymentAmount != required
                )
                {
                    throw new BusinessException(
                        BuildingManagementErrorCodes
                            .InvalidReservationDepositPaymentAmount
                    );
                }

                return;
            }

            /*
             * Đã đóng 500k:
             * lần tiếp theo phải đóng đúng phần còn thiếu.
             *
             * Ví dụ:
             * Required = 3m
             * Paid     = 500k
             * Next     = 2.5m
             */
            if (completedAmount == minimum)
            {
                var remaining =
                    required
                    - completedAmount;

                if (paymentAmount != remaining)
                {
                    throw new BusinessException(
                        BuildingManagementErrorCodes
                            .InvalidReservationDepositPaymentAmount
                    );
                }

                return;
            }

            /*
             * Đã đủ cọc.
             */
            if (completedAmount >= required)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ReservationDepositAlreadyFullyPaid
                );
            }

            /*
             * Nếu rơi vào đây nghĩa là DB đang có
             * trạng thái tiền cọc không hợp lệ,
             * ví dụ completedAmount = 1.5m.
             */
            throw new BusinessException(
                BuildingManagementErrorCodes
                    .InvalidReservationDepositPaymentAmount
            );
        }

        public async Task<PaymentDto> GetAsync(Guid id)
        {
            var payment =
                await GetExistingPaymentAsync(id);

            return ObjectMapper.Map<
                Payment,
                PaymentDto
            >(payment);
        }

        public async Task<List<PaymentDto>> GetListAsync()
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

        public async Task<PaymentDto> CreateReservationDepositAsync(CreateReservationDepositPaymentDto input)
        {
            var paymentNumber =
                NormalizeCode(input.PaymentNumber);

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

            /*
             * Business rule:
             *
             * Chưa thanh toán:
             * - chỉ được đóng 500k
             * - hoặc đóng đủ RequiredDepositAmount
             *
             * Đã đóng 500k:
             * - lần tiếp theo phải đóng đúng phần còn thiếu
             *
             * Không cho phép các mức trung gian.
             */
            ValidateReservationDepositPaymentAmount(
                reservation,
                totalPaid,
                input.Amount
            );

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


        [UnitOfWork(isTransactional: true)]
        public virtual async Task<PaymentDto> CompleteAsync(Guid id, CompletePaymentDto input)
        {
            var payment = await GetExistingPaymentAsync(id);

            if (payment.Status != PaymentStatus.Pending)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.PaymentCannotBeCompleted
                );
            }

            /*
             * ContractDeposit sử dụng workflow riêng.
             */
            if (payment.Purpose == PaymentPurpose.ContractDeposit)
            {
                return await CompleteContractDepositAsync(
                    payment,
                    input
                );
            }

            if (
                payment.Purpose != PaymentPurpose.ReservationDeposit ||
                !payment.RoomReservationId.HasValue
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.PaymentCannotBeCompleted
                );
            }

            /*
             * Shared gate for an occupied room:
             * - First reservation deposit takes the Active Contract row lock.
             * - Creating a renewal hold takes the SAME Contract row lock.
             *
             * Lookup IDs with scalar projections so the Reservation is not
             * attached to EF's change tracker before FindForUpdateAsync.
             * Payment lock order: Active Contract -> Reservation -> Payment.
             */
            var reservationSnapshotQuery =
                (await _roomReservationRepository.GetQueryableAsync())
                .Where(x => x.Id == payment.RoomReservationId.Value)
                .Select(x => new { x.RoomId, x.Status });
            var reservationSnapshot = await AsyncExecuter.FirstOrDefaultAsync(
                reservationSnapshotQuery);
            var roomId = reservationSnapshot?.RoomId ?? Guid.Empty;

            if (roomId != Guid.Empty)
            {
                var activeContractIdQuery = (await _contractRepository.GetQueryableAsync())
                    .Where(x => x.RoomId == roomId &&
                                x.Status == ContractStatus.Active)
                    .Select(x => x.Id);

                var activeContractId = await AsyncExecuter.FirstOrDefaultAsync(
                    activeContractIdQuery);

                if (activeContractId != Guid.Empty)
                {
                    var lockedContract = await _contractLockRepository
                        .FindForUpdateAsync(activeContractId);

                    if (lockedContract is null)
                    {
                        throw new BusinessException(
                            BuildingManagementErrorCodes.RoomNotAvailableForReservation);
                    }
                }
            }

            // Fast-fail a first deposit against a Hold that already exists.
            // Keep the later check under Reservation lock too. Avoid taking
            // Reservation here when renewal may be waiting for that row.
            if (reservationSnapshot?.Status == RoomReservationStatus.PendingPayment)
            {
                await EnsureNoActiveRenewalHoldAsync(roomId);
            }

            /*
             * Lock Reservation after the shared Contract gate. This still
             * serializes multiple pending Payments for the same Reservation.
             */
            var reservation =
                await _roomReservationLockRepository.FindForUpdateAsync(
                    payment.RoomReservationId.Value
                );

            if (reservation is null)
            {
                await GetExistingReservationAsync(
                    payment.RoomReservationId.Value
                );

                throw new InvalidOperationException(
                    "Unable to lock the room reservation."
                );
            }

            EnsureReservationIsPayable(reservation);

            if (reservation.TenantId != payment.TenantId)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.PaymentCannotBeCompleted
                );
            }

            /*
             * Kiểm tra lại điều kiện giữ phòng khi Reservation
             * vẫn đang PendingPayment.
             *
             * Không áp dụng kiểm tra này cho Reservation đã
             * Reserved và đang thanh toán phần cọc còn lại.
             */
            if (reservation.Status == RoomReservationStatus.PendingPayment)
            {
                await EnsureNoOtherReservedReservationAsync(
                    reservation
                );

                await EnsureNoActiveRenewalHoldAsync(
                    reservation.RoomId
                );

                /*
                 * Kiểm tra lại trạng thái vật lý của phòng.
                 */
                var room = await _roomRepository.FindAsync(
                    reservation.RoomId
                );

                if (room is null)
                {
                    throw new BusinessException(
                        BuildingManagementErrorCodes.RoomNotFound
                    );
                }

                if (
                    room.Status == RoomStatus.Maintenance ||
                    room.Status == RoomStatus.Inactive
                )
                {
                    throw new BusinessException(
                        BuildingManagementErrorCodes
                            .RoomNotAvailableForReservation
                    );
                }

                /*
                 * Phòng đã Reserved không được tiếp nhận
                 * khoản cọc giữ phòng đầu tiên của khách khác.
                 */
                if (room.Status == RoomStatus.Reserved)
                {
                    throw new BusinessException(
                        BuildingManagementErrorCodes.RoomAlreadyReserved
                    );
                }


                /*
                 * Kiem tra Signed Contract phat sinh sau
                 * khi Reservation duoc tao.
                 *
                 * Signed Contract co EndDate truoc
                 * ExpectedMoveInDate thi khong xung dot.
                 *
                 * Signed Contract chua co EndDate
                 * hoac EndDate >= ExpectedMoveInDate
                 * thi khong duoc xac nhan coc.
                 */
                var hasConflictingSignedContract =
                    await _contractRepository.AnyAsync(
                        x =>
                            x.RoomId == room.Id &&
                            x.Status == ContractStatus.Signed &&
                            (
                                !x.EndDate.HasValue ||
                                x.EndDate.Value.Date >=
                                    reservation.ExpectedMoveInDate.Date
                            )
                    );

                if (hasConflictingSignedContract)
                {
                    throw new BusinessException(
                        BuildingManagementErrorCodes
                            .RoomNotAvailableForReservation
                    );
                }


                /*
                 * KIỂM TRA MỚI:
                 *
                 * Phòng có thể đã phát sinh Active Contract
                 * sau thời điểm khách tạo Reservation.
                 *
                 * Phải kiểm tra lại trước khi nhận tiền cọc.
                 */
                var activeContract =
                    await _contractRepository.FirstOrDefaultAsync(
                        x =>
                            x.RoomId == room.Id &&
                            x.Status == ContractStatus.Active
                    );

                if (activeContract is not null)
                {
                    /*
                     * Chỉ được giữ phòng nếu:
                     *
                     * 1. Phòng thực sự đang Occupied.
                     * 2. Active Contract có EndDate.
                     * 3. EndDate < ExpectedMoveInDate.
                     */
                    if (
                        room.Status != RoomStatus.Occupied ||
                        !activeContract.EndDate.HasValue ||
                        activeContract.EndDate.Value.Date >=
                            reservation.ExpectedMoveInDate.Date
                    )
                    {
                        throw new BusinessException(
                            BuildingManagementErrorCodes
                                .RoomNotAvailableForReservation
                        );
                    }
                }
                else if (room.Status == RoomStatus.Occupied)
                {
                    /*
                     * Phòng Occupied nhưng không có
                     * Active Contract hợp lệ.
                     */
                    throw new BusinessException(
                        BuildingManagementErrorCodes
                            .RoomNotAvailableForReservation
                    );
                }
            }

            /*
             * Sau khi lock Reservation, tính lại tổng tiền
             * đã hoàn tất.
             */
            var totalPaid =
                await GetCompletedReservationDepositTotalAsync(
                    reservation.Id
                );

            var remaining =
                reservation.RequiredDepositAmount - totalPaid;

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

            /*
             * Quy tắc thanh toán:
             *
             * 0    -> 500.000 VND
             * 0    -> 100% RequiredDepositAmount
             * 500k -> Chính xác số tiền còn thiếu
             *
             * Không cho phép mức thanh toán trung gian.
             */
            ValidateReservationDepositPaymentAmount(
                reservation,
                totalPaid,
                payment.Amount
            );

            var inputTransactionReference =
                NormalizeOptional(input.TransactionReference);

            var effectiveTransactionReference =
                inputTransactionReference
                ?? payment.TransactionReference;

            if (
                payment.Method != PaymentMethod.Cash &&
                string.IsNullOrWhiteSpace(effectiveTransactionReference)
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

            /*
             * Tất cả điều kiện hợp lệ.
             * Bắt đầu hoàn tất thanh toán.
             */
            var paidAt = _clock.Now;

            payment.Complete(
                paidAt,
                inputTransactionReference
            );

            await _paymentRepository.UpdateAsync(
                payment,
                autoSave: true
            );

            /*
             * Chỉ khoản cọc đầu tiên mới chuyển Reservation
             * từ PendingPayment sang Reserved.
             */
            if (reservation.Status == RoomReservationStatus.PendingPayment)
            {
                reservation.MarkReserved(
                    paidAt,
                    GetEndOfMonth(paidAt)
                );

                await _roomReservationRepository.UpdateAsync(
                    reservation,
                    autoSave: true
                );

                var room = await _roomRepository.FindAsync(
                    reservation.RoomId
                );

                if (room is null)
                {
                    throw new BusinessException(
                        BuildingManagementErrorCodes.RoomNotFound
                    );
                }

                /*
                 * Chỉ đổi trạng thái vật lý khi phòng Available.
                 *
                 * Nếu phòng đang Occupied bởi khách cũ
                 * và lịch nhận phòng mới hợp lệ,
                 * tiếp tục giữ nguyên Occupied.
                 */
                if (room.Status == RoomStatus.Available)
                {
                    room.ChangeStatus(RoomStatus.Reserved);

                    await _roomRepository.UpdateAsync(
                        room,
                        autoSave: true
                    );
                }
            }

            return ObjectMapper.Map<Payment, PaymentDto>(payment);
        }


        private async Task<PaymentDto> CompleteContractDepositAsync(Payment payment, CompletePaymentDto input)
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

        public async Task<PaymentDto>CancelAsync(Guid id)
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

        private async Task<Payment> GetExistingPaymentAsync(Guid id)
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

        private async Task<RoomReservation>GetExistingReservationAsync(Guid id)
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

        private static void EnsureReservationIsPayable(RoomReservation reservation)
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

        private async Task EnsureNoOtherReservedReservationAsync(RoomReservation reservation)
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

        private async Task EnsureNoActiveRenewalHoldAsync(Guid roomId)
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

        private async Task<decimal> GetCompletedReservationDepositTotalAsync(Guid reservationId)
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

        private async Task EnsurePaymentNumberIsUniqueAsync(string paymentNumber)
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

        private async Task EnsureTransactionReferenceIsUniqueAsync(string transactionReference,
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

        private static void ValidatePaymentMethodAndChannel(PaymentMethod method, PaymentChannel channel)
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

        private static DateTime GetEndOfMonth(DateTime date)
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

        private static string NormalizeCode(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value
                    .Trim()
                    .ToUpperInvariant();
        }

        private static string? NormalizeOptional(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim();
        }
    }
}