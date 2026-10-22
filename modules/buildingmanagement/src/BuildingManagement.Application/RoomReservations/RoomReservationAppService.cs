using BuildingManagement.Contracts;
using BuildingManagement.Payments;
using BuildingManagement.Rooms;
using BuildingManagement.Tenants;
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

namespace BuildingManagement.RoomReservations
{
    public class RoomReservationAppService
        : ApplicationService,
          IRoomReservationAppService
    {
        private readonly IRepository<RoomReservation, Guid>
            _roomReservationRepository;

        private readonly IRepository<Room, Guid>
            _roomRepository;

        private readonly IRepository<Tenant, Guid>
            _tenantRepository;

        private readonly IRepository<Contract, Guid>
            _contractRepository;

        private readonly IDataFilter<ISoftDelete>
            _softDeleteFilter;

        private readonly IClock
            _clock;

        private readonly IRoomReservationRepository
            _roomReservationLockRepository;

        private readonly IRepository<Payment, Guid>
            _paymentRepository;

        private readonly IPaymentAppService
            _paymentAppService;

        public RoomReservationAppService(
            IRepository<RoomReservation, Guid>
                roomReservationRepository,
            IRepository<Room, Guid>
                roomRepository,
            IRepository<Tenant, Guid>
                tenantRepository,
            IRepository<Contract, Guid>
                contractRepository,
            IDataFilter<ISoftDelete>
                softDeleteFilter,
            IClock clock,
            IRoomReservationRepository
                roomReservationLockRepository,
            IRepository<Payment, Guid>
                paymentRepository,
            IPaymentAppService paymentAppService)
        {
            _roomReservationRepository =
                roomReservationRepository;

            _roomRepository =
                roomRepository;

            _tenantRepository =
                tenantRepository;

            _contractRepository =
                contractRepository;

            _softDeleteFilter =
                softDeleteFilter;

            _clock =
                clock;

            _roomReservationLockRepository =
                roomReservationLockRepository;

            _paymentRepository =
                paymentRepository;

            _paymentAppService =
                paymentAppService;
        }

        public async Task<RoomReservationDto> GetAsync(Guid id)
        {
            var reservation =
                await GetExistingReservationAsync(
                    id
                );

            return ObjectMapper.Map<
                RoomReservation,
                RoomReservationDto
            >(reservation);
        }

        public async Task<List<RoomReservationDto>> GetListAsync()
        {
            var queryable =
                await _roomReservationRepository
                    .GetQueryableAsync();

            var reservations =
                await AsyncExecuter.ToListAsync(
                    queryable.OrderByDescending(
                        x => x.CreationTime
                    )
                );

            return ObjectMapper.Map<
                List<RoomReservation>,
                List<RoomReservationDto>
            >(reservations);
        }

        public async Task<RoomReservationDto> CreateAsync(CreateRoomReservationDto input)
        {
            var reservationNumber =
                NormalizeReservationNumber(
                    input.ReservationNumber
                );

            if (string.IsNullOrWhiteSpace(
                reservationNumber))
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidRoomReservationNumber
                );
            }

            if (input.QuotedMonthlyRent <= 0)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidQuotedMonthlyRent
                );
            }

            if (input.RequiredDepositAmount <= 0)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidRequiredDepositAmount
                );
            }

            if (input.MinimumReservationDepositAmount <= 0
                ||
                input.MinimumReservationDepositAmount >
                    input.RequiredDepositAmount
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidMinimumReservationDepositAmount
                );
            }

            var expectedMoveInDate =
                input.ExpectedMoveInDate.Date;

            var room =
                await _roomRepository.FindAsync(
                    input.RoomId
                );

            if (room is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .RoomNotFound
                );
            }

            var tenant =
                await _tenantRepository.FindAsync(
                    input.TenantId
                );

            if (tenant is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .TenantNotFound
                );
            }

            await EnsureReservationNumberIsUniqueAsync(
                reservationNumber
            );

            await EnsureTenantDoesNotAlreadyHaveReservationAsync(
                room.Id,
                tenant.Id
            );

            await EnsureRoomCanBeReservedAsync(
                room,
                expectedMoveInDate
            );

            var reservation =
                new RoomReservation(
                    GuidGenerator.Create(),
                    reservationNumber,
                    room.Id,
                    tenant.Id,
                    expectedMoveInDate,
                    input.QuotedMonthlyRent,
                    input.RequiredDepositAmount,
                    input.MinimumReservationDepositAmount,
                    NormalizeOptional(
                        input.Notes
                    )
                );

            await _roomReservationRepository
                .InsertAsync(
                    reservation,
                    autoSave: true
                );

            return ObjectMapper.Map<
                RoomReservation,
                RoomReservationDto
            >(reservation);
        }

        [UnitOfWork(isTransactional: true)]
        public virtual async Task<RoomReservationDto> CancelAsync(Guid id, CancelRoomReservationDto input)
        {
            // Take the reservation lock before checking or changing its state.
            var reservation =
                await _roomReservationLockRepository.FindForUpdateAsync(id);

            if (reservation is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.RoomReservationNotFound
                );
            }

            if (
                reservation.Status != RoomReservationStatus.PendingPayment
                &&
                reservation.Status != RoomReservationStatus.Reserved
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.RoomReservationCannotBeCancelled
                );
            }

            // Preserve the cancellation reasons supported by this API.
            if (
                !Enum.IsDefined(typeof(ReservationCancellationReason), input.Reason)
                ||
                input.Reason == ReservationCancellationReason.NoShow
                ||
                input.Reason == ReservationCancellationReason.SupersededByRenewal
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.InvalidReservationCancellationReason
                );
            }

            var hasCompletedDeposit =
                await _paymentRepository.AnyAsync(
                    payment =>
                        payment.RoomReservationId == reservation.Id
                        &&
                        payment.Purpose == PaymentPurpose.ReservationDeposit
                        &&
                        payment.Status == PaymentStatus.Completed
                );

            reservation.Cancel(
                _clock.Now,
                input.Reason,
                NormalizeOptional(input.Notes)
            );

            // Flush the cancelled state without committing the enclosing UOW.
            await _roomReservationRepository.UpdateAsync(
                reservation,
                autoSave: true
            );

            // No completed deposit means there is no money to settle.
            // Do not swallow settlement errors or start a separate transaction.
            if (hasCompletedDeposit)
            {
                await _paymentAppService.SettleCancelledReservationDepositAsync(
                    reservation.Id
                );
            }

            return ObjectMapper.Map<RoomReservation, RoomReservationDto>(reservation);
        }

        private async Task<RoomReservation> GetExistingReservationAsync(Guid id)
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

        private async Task EnsureReservationNumberIsUniqueAsync(string reservationNumber)
        {
            bool exists;

            using (_softDeleteFilter.Disable())
            {
                exists =
                    await _roomReservationRepository
                        .AnyAsync(
                            x =>
                                x.ReservationNumber ==
                                    reservationNumber
                        );
            }

            if (exists)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .RoomReservationNumberAlreadyExists
                );
            }
        }

        private async Task EnsureTenantDoesNotAlreadyHaveReservationAsync(Guid roomId, Guid tenantId)
        {
            var exists =
                await _roomReservationRepository
                    .AnyAsync(
                        x =>
                            x.RoomId == roomId
                            &&
                            x.TenantId == tenantId
                            &&
                            (
                                x.Status ==
                                    RoomReservationStatus
                                        .PendingPayment
                                ||
                                x.Status ==
                                    RoomReservationStatus
                                        .Reserved
                            )
                    );

            if (exists)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .RoomReservationAlreadyExistsForTenant
                );
            }
        }

        private async Task EnsureRoomCanBeReservedAsync(Room room, DateTime expectedMoveInDate)
        {
            if (
                room.Status ==
                    RoomStatus.Maintenance
                ||
                room.Status ==
                    RoomStatus.Inactive
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .RoomNotAvailableForReservation
                );
            }

            var hasReservedReservation =
                await _roomReservationRepository
                    .AnyAsync(
                        x =>
                            x.RoomId == room.Id
                            &&
                            x.Status ==
                                RoomReservationStatus
                                    .Reserved
                    );

            if (hasReservedReservation)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .RoomAlreadyReserved
                );
            }

            var hasSignedContract =
                await _contractRepository
                    .AnyAsync(
                        x =>
                            x.RoomId == room.Id
                            &&
                            x.Status ==
                                ContractStatus.Signed
                    );

            if (hasSignedContract)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .RoomNotAvailableForReservation
                );
            }

            var activeContract =
                await _contractRepository
                    .FirstOrDefaultAsync(
                        x =>
                            x.RoomId == room.Id
                            &&
                            x.Status ==
                                ContractStatus.Active
                    );

            if (activeContract is not null)
            {
                // A future reservation is permitted only while the old
                // occupant is still recorded as occupying the room.
                if (room.Status != RoomStatus.Occupied ||
                    !activeContract.EndDate.HasValue ||
                    activeContract.EndDate.Value.Date >= expectedMoveInDate)
                {
                    throw new BusinessException(
                        BuildingManagementErrorCodes.RoomNotAvailableForReservation
                    );
                }
            }
            else
            {
                if (room.Status == RoomStatus.Occupied)
                {
                    throw new BusinessException(
                        BuildingManagementErrorCodes.RoomNotAvailableForReservation
                    );
                }

                if (room.Status == RoomStatus.Reserved)
                {
                    throw new BusinessException(
                        BuildingManagementErrorCodes.RoomAlreadyReserved
                    );
                }
            }

            // The business calendar is Vietnam time, not the UTC date.
            // Use the already-tested domain policy for the inclusive cutoff.
            var todayInVietnam = GetTodayInVietnam();
            var firstDayOfNextMonth = new DateOnly(
                todayInVietnam.Year, todayInVietnam.Month, 1).AddMonths(1);
            var earliest = activeContract is null
                ? todayInVietnam
                : firstDayOfNextMonth;
            var window = new MoveInDateWindow(
                earliest,
                MoveInDatePolicy.GetNextMonthCutoff(todayInVietnam));
            var selected = DateOnly.FromDateTime(expectedMoveInDate);

            if (!window.Contains(selected))
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes.InvalidExpectedMoveInDate
                );
            }
        }

        private DateOnly GetTodayInVietnam()
        {
            var nowUtc = _clock.Now;
            if (nowUtc.Kind != DateTimeKind.Utc)
            {
                throw new AbpException(
                    "IClock must use UTC for move-in date validation.");
            }

            var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById(
                "Asia/Ho_Chi_Minh");
            var localNow = TimeZoneInfo.ConvertTimeFromUtc(
                nowUtc, vietnamTimeZone);
            return DateOnly.FromDateTime(localNow);
        }

        private static string NormalizeReservationNumber(string? value)
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