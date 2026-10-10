using BuildingManagement.RoomReservations;
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

namespace BuildingManagement.Contracts
{
    public class ContractAppService
        : ApplicationService,
          IContractAppService
    {
        private readonly IRepository<Contract, Guid> _contractRepository;

        private readonly IRepository<ContractTenant, Guid> _contractTenantRepository;

        private readonly IRepository<Room, Guid> _roomRepository;

        private readonly IRepository<Tenant, Guid> _tenantRepository;

        private readonly IDataFilter<ISoftDelete> _softDeleteFilter;

        private readonly IRepository<ContractRenewalHold, Guid> _contractRenewalHoldRepository;

        private readonly IClock _clock;

        private readonly IRepository<RoomReservation, Guid> _roomReservationRepository;

        public ContractAppService(
            IRepository<Contract, Guid> contractRepository,
            IRepository<ContractTenant, Guid> contractTenantRepository,
            IRepository<Room, Guid> roomRepository,
            IRepository<Tenant, Guid> tenantRepository,
            IDataFilter<ISoftDelete> softDeleteFilter,
            IRepository<ContractRenewalHold, Guid> contractRenewalHoldRepository,
            IClock clock, IRepository<RoomReservation, Guid>roomReservationRepository)
        {
            _contractRepository = contractRepository;
            _contractTenantRepository = contractTenantRepository;
            _roomRepository = roomRepository;
            _tenantRepository = tenantRepository;
            _softDeleteFilter = softDeleteFilter;
            _contractRenewalHoldRepository = contractRenewalHoldRepository;
            _clock = clock;
            _roomReservationRepository = roomReservationRepository;
        }

        public async Task<ContractDto> GetAsync(
            Guid id)
        {
            var contract =
                await GetExistingContractAsync(id);

            return ObjectMapper.Map<
                Contract,
                ContractDto
            >(contract);
        }

        public async Task<List<ContractDto>>
            GetListAsync()
        {
            var contracts =
                await _contractRepository.GetListAsync();

            return ObjectMapper.Map<
                List<Contract>,
                List<ContractDto>
            >(contracts);
        }

        public async Task<ContractDto> CreateAsync(
            CreateContractDto input)
        {
            await EnsureRoomExistsAsync(
                input.RoomId
            );

            ValidateContractValues(
                input.StartDate,
                input.EndDate,
                input.MonthlyRent,
                input.DepositAmount
            );

            var contractNumber =
                NormalizeContractNumber(
                    input.ContractNumber
                );

            await EnsureContractNumberUniqueAsync(
                contractNumber
            );

            var contract = new Contract(
                GuidGenerator.Create(),
                contractNumber,
                input.RoomId,
                input.StartDate.Date,
                input.EndDate?.Date,
                input.MonthlyRent,
                input.DepositAmount,
                NormalizeOptional(input.Notes)
            );

            await _contractRepository.InsertAsync(
                contract,
                autoSave: true
            );

            return ObjectMapper.Map<
                Contract,
                ContractDto
            >(contract);
        }

        public async Task<ContractDto> UpdateAsync(
            Guid id,
            UpdateContractDto input)
        {
            var contract =
                await GetExistingContractAsync(id);

            EnsureContractIsDraft(contract);

            await EnsureRoomExistsAsync(
                input.RoomId
            );

            ValidateContractValues(
                input.StartDate,
                input.EndDate,
                input.MonthlyRent,
                input.DepositAmount
            );

            var contractNumber =
                NormalizeContractNumber(
                    input.ContractNumber
                );

            await EnsureContractNumberUniqueAsync(
                contractNumber,
                contract.Id
            );

            contract.Update(
                contractNumber,
                input.RoomId,
                input.StartDate.Date,
                input.EndDate?.Date,
                input.MonthlyRent,
                input.DepositAmount,
                NormalizeOptional(input.Notes)
            );

            await _contractRepository.UpdateAsync(
                contract,
                autoSave: true
            );

            return ObjectMapper.Map<
                Contract,
                ContractDto
            >(contract);
        }

        public async Task<List<ContractTenantDto>>
            GetTenantsAsync(
                Guid id)
        {
            await GetExistingContractAsync(
                id
            );

            var tenants =
                await _contractTenantRepository
                    .GetListAsync(
                        x => x.ContractId ==
                             id
                    );

            return ObjectMapper.Map<
                List<ContractTenant>,
                List<ContractTenantDto>
            >(tenants);
        }

        public async Task<ContractTenantDto>AddTenantAsync(Guid id, AddContractTenantDto input)
        {
            var contract = await GetExistingContractAsync(id);

            EnsureContractIsDraft(contract);

            await EnsureTenantExistsAsync(
                input.TenantId
            );

            ValidateContractTenantRole(
                input.Role
            );

            ContractTenant? existingRelation;

            using (_softDeleteFilter.Disable())
            {
                existingRelation =
                    await _contractTenantRepository
                        .FindAsync(
                            x =>
                                x.ContractId ==
                                    id
                                &&
                                x.TenantId ==
                                    input.TenantId
                        );
            }

            if (existingRelation is not null &&
                !existingRelation.IsDeleted)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .TenantAlreadyInContract
                );
            }

            if (input.Role ==
                ContractTenantRole.PrimaryTenant)
            {
                await EnsurePrimaryTenantAvailableAsync(
                    id
                );
            }

            /*
             * Nếu người này trước đây đã bị remove
             * khỏi Contract thì restore record cũ,
             * không insert record mới để tránh
             * unique constraint.
             */
            if (existingRelation is not null)
            {
                existingRelation.IsDeleted = false;
                existingRelation.DeleterId = null;
                existingRelation.DeletionTime = null;

                existingRelation.UpdateRole(
                    input.Role
                );

                await _contractTenantRepository
                    .UpdateAsync(
                        existingRelation,
                        autoSave: true
                    );

                return ObjectMapper.Map<
                    ContractTenant,
                    ContractTenantDto
                >(existingRelation);
            }

            var contractTenant =
                new ContractTenant(
                    GuidGenerator.Create(),
                    id,
                    input.TenantId,
                    input.Role
                );

            await _contractTenantRepository
                .InsertAsync(
                    contractTenant,
                    autoSave: true
                );

            return ObjectMapper.Map<
                ContractTenant,
                ContractTenantDto
            >(contractTenant);
        }

        public async Task<ContractTenantDto>UpdateTenantRoleAsync(Guid id, Guid tenantId, UpdateContractTenantRoleDto input)
        {
            var contract = await GetExistingContractAsync(id);

            EnsureContractIsDraft(contract);

            ValidateContractTenantRole(
                input.Role
            );

            var contractTenant =
                await _contractTenantRepository
                    .FindAsync(
                        x =>
                            x.ContractId ==
                                id
                            &&
                            x.TenantId ==
                                tenantId
                    );

            if (contractTenant is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractTenantNotFound
                );
            }

            if (input.Role ==
                ContractTenantRole.PrimaryTenant)
            {
                await EnsurePrimaryTenantAvailableAsync(
                    id,
                    contractTenant.Id
                );
            }

            contractTenant.UpdateRole(
                input.Role
            );

            await _contractTenantRepository
                .UpdateAsync(
                    contractTenant,
                    autoSave: true
                );

            return ObjectMapper.Map<
                ContractTenant,
                ContractTenantDto
            >(contractTenant);
        }

        public async Task RemoveTenantAsync(Guid id, Guid tenantId)
        {
            var contract = await GetExistingContractAsync(id);

            EnsureContractIsDraft(contract);

            var contractTenant =
                await _contractTenantRepository
                    .FindAsync(
                        x =>
                            x.ContractId ==
                                id
                            &&
                            x.TenantId ==
                                tenantId
                    );

            if (contractTenant is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractTenantNotFound
                );
            }

            await _contractTenantRepository
                .DeleteAsync(
                    contractTenant,
                    autoSave: true
                );
        }

        private async Task<Contract>
            GetExistingContractAsync(
                Guid id)
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

        private async Task EnsureRoomExistsAsync(
            Guid roomId)
        {
            var exists =
                await _roomRepository.AnyAsync(
                    x => x.Id == roomId
                );

            if (!exists)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .RoomNotFound
                );
            }
        }

        private async Task EnsureTenantExistsAsync(
            Guid tenantId)
        {
            var exists =
                await _tenantRepository.AnyAsync(
                    x => x.Id == tenantId
                );

            if (!exists)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .TenantNotFound
                );
            }
        }

        private async Task
            EnsureContractNumberUniqueAsync(
                string contractNumber,
                Guid? excludedContractId = null)
        {
            bool exists;

            using (_softDeleteFilter.Disable())
            {
                exists =
                    await _contractRepository.AnyAsync(
                        x =>
                            x.ContractNumber ==
                                contractNumber
                            &&
                            (!excludedContractId
                                .HasValue
                             ||
                             x.Id !=
                                excludedContractId
                                    .Value)
                    );
            }

            if (exists)
            {
                throw new BusinessException(
                        BuildingManagementErrorCodes
                            .ContractNumberAlreadyExists
                    )
                    .WithData(
                        "ContractNumber",
                        contractNumber
                    );
            }
        }

        private async Task
            EnsurePrimaryTenantAvailableAsync(
                Guid contractId,
                Guid? excludedContractTenantId =
                    null)
        {
            var exists =
                await _contractTenantRepository
                    .AnyAsync(
                        x =>
                            x.ContractId ==
                                contractId
                            &&
                            x.Role ==
                                ContractTenantRole
                                    .PrimaryTenant
                            &&
                            (!excludedContractTenantId
                                .HasValue
                             ||
                             x.Id !=
                                excludedContractTenantId
                                    .Value)
                    );

            if (exists)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractAlreadyHasPrimaryTenant
                );
            }
        }

        private static void
            ValidateContractValues(
                DateTime startDate,
                DateTime? endDate,
                decimal monthlyRent,
                decimal depositAmount)
        {
            if (endDate.HasValue &&
                endDate.Value.Date <
                startDate.Date)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidContractPeriod
                );
            }

            if (monthlyRent <= 0)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidMonthlyRent
                );
            }

            if (depositAmount < 0)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidDepositAmount
                );
            }
        }

        private static void
            ValidateContractTenantRole(
                ContractTenantRole role)
        {
            if (!Enum.IsDefined(role))
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidContractTenantRole
                );
            }
        }

        private static string
            NormalizeContractNumber(
                string value)
        {
            return value
                .Trim()
                .ToUpperInvariant();
        }

        private static string?
            NormalizeOptional(
                string? value)
        {
            return string.IsNullOrWhiteSpace(
                value
            )
                ? null
                : value.Trim();
        }

        public async Task<ContractDto> ActivateAsync(Guid id)
        {
            var contract =
                await GetExistingContractAsync(id);

            if (contract.Status != ContractStatus.Draft &&
                contract.Status != ContractStatus.Signed)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractCannotBeActivated
                );
            }

            await EnsureContractHasValidTenantsAsync(
                contract.Id
            );

            var room =
                await _roomRepository.FindAsync(
                    contract.RoomId
                );

            if (room is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .RoomNotFound
                );
            }

            var hasAnotherActiveContract =
                await _contractRepository.AnyAsync(
                    x =>
                        x.RoomId == contract.RoomId
                        &&
                        x.Status == ContractStatus.Active
                        &&
                        x.Id != contract.Id
                );

            if (hasAnotherActiveContract)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .RoomAlreadyHasActiveContract
                );
            }

            await EnsureNoOverlappingCommittedContractAsync(
                contract.RoomId,
                contract.StartDate,
                contract.EndDate,
                contract.Id
            );

            if (room.Status != RoomStatus.Available &&
                room.Status != RoomStatus.Reserved)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .RoomNotAvailableForRent
                );
            }

            contract.Activate();

            room.ChangeStatus(
                RoomStatus.Occupied
            );

            await _contractRepository.UpdateAsync(
                contract,
                autoSave: true
            );

            await _roomRepository.UpdateAsync(
                room,
                autoSave: true
            );

            return ObjectMapper.Map<
                Contract,
                ContractDto
            >(contract);
        }

        public async Task<ContractDto> EndAsync(Guid id)
        {
            var contract =
                await GetExistingContractAsync(id);

            if (contract.Status != ContractStatus.Active)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractCannotBeEnded
                );
            }

            var room =
                await _roomRepository.FindAsync(
                    contract.RoomId
                );

            if (room is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .RoomNotFound
                );
            }

            contract.End();

            if (room.Status == RoomStatus.Occupied)
            {
                var hasFutureCommitment =
                    await HasFutureCommittedOccupancyAsync(
                        room.Id
                    );

                room.ChangeStatus(
                    hasFutureCommitment
                        ? RoomStatus.Reserved
                        : RoomStatus.Available
                );

                await _roomRepository.UpdateAsync(
                    room,
                    autoSave: true
                );
            }

            await _contractRepository.UpdateAsync(
                contract,
                autoSave: true
            );

            return ObjectMapper.Map<
                Contract,
                ContractDto
            >(contract);
        }

        public async Task<ContractDto> CancelAsync(Guid id)
        {
            var contract =
                await GetExistingContractAsync(id);

            if (contract.Status != ContractStatus.Draft &&
    contract.Status != ContractStatus.Signed)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractCannotBeCancelled
                );
            }

            contract.Cancel();

            await _contractRepository.UpdateAsync(
                contract,
                autoSave: true
            );

            return ObjectMapper.Map<
                Contract,
                ContractDto
            >(contract);
        }

        private static void EnsureContractIsDraft(Contract contract)
        {
            if (contract.Status != ContractStatus.Draft)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractCanOnlyBeModifiedWhenDraft
                );
            }
        }

        private async Task EnsureContractHasValidTenantsAsync(
    Guid contractId)
        {
            var contractTenants =
                await _contractTenantRepository.GetListAsync(
                    x => x.ContractId == contractId
                );

            if (contractTenants.Count == 0)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractRequiresTenant
                );
            }

            var primaryTenantCount =
                contractTenants.Count(
                    x =>
                        x.Role ==
                        ContractTenantRole.PrimaryTenant
                );

            if (primaryTenantCount != 1)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractRequiresPrimaryTenant
                );
            }
        }

        private async Task
    EnsureNoOverlappingCommittedContractAsync(
        Guid roomId,
        DateTime startDate,
        DateTime? endDate,
        Guid? excludedContractId = null)
        {
            var start = startDate.Date;
            var end = endDate?.Date;

            var exists =
                await _contractRepository.AnyAsync(
                    x =>
                        x.RoomId == roomId
                        &&
                        (
                            x.Status == ContractStatus.Active
                            ||
                            x.Status == ContractStatus.Signed
                        )
                        &&
                        (
                            !excludedContractId.HasValue
                            ||
                            x.Id != excludedContractId.Value
                        )
                        &&
                        (
                            !x.EndDate.HasValue
                            ||
                            x.EndDate.Value >= start
                        )
                        &&
                        (
                            !end.HasValue
                            ||
                            x.StartDate <= end.Value
                        )
                );

            if (exists)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .RoomHasOverlappingCommittedContract
                );
            }
        }

        public async Task<ContractDto> SignAsync(Guid id)
        {
            var contract =
                await GetExistingContractAsync(id);

            if (contract.Status != ContractStatus.Draft)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractCannotBeSigned
                );
            }

            await EnsureContractHasValidTenantsAsync(
                contract.Id
            );

            await EnsureNoOverlappingCommittedContractAsync(
                contract.RoomId,
                contract.StartDate,
                contract.EndDate,
                contract.Id
            );

            contract.Sign();

            await _contractRepository.UpdateAsync(
                contract,
                autoSave: true
            );

            return ObjectMapper.Map<
                Contract,
                ContractDto
            >(contract);
        }

        private async Task EnsureContractNotAlreadyRenewedAsync(Guid contractId)
        {
            bool exists;

            using (_softDeleteFilter.Disable())
            {
                exists =
                    await _contractRepository.AnyAsync(
                        x =>
                            x.RenewedFromContractId ==
                            contractId
                    );
            }

            if (exists)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractAlreadyRenewed
                );
            }
        }

        public async Task<ContractDto> RenewAsync(Guid id, RenewContractDto input)
        {
            var currentContract =
                await GetExistingContractAsync(id);

            if (currentContract.Status !=
                ContractStatus.Active)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractCannotBeRenewed
                );
            }

            if (!currentContract.EndDate.HasValue)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractCannotBeRenewed
                );
            }

            ValidateContractValues(
                input.StartDate,
                input.EndDate,
                input.MonthlyRent,
                input.DepositAmount
            );

            if (input.StartDate.Date <=
                currentContract.EndDate.Value.Date)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidRenewalContractPeriod
                );
            }

            await EnsureContractNotAlreadyRenewedAsync(
                currentContract.Id
            );

            var renewalHold = await GetValidRenewalHoldAsync(currentContract.Id);

            await EnsureContractHasValidTenantsAsync(
                currentContract.Id
            );

            var contractNumber =
                NormalizeContractNumber(
                    input.ContractNumber
                );

            await EnsureContractNumberUniqueAsync(
                contractNumber
            );

            await EnsureNoOverlappingCommittedContractAsync(
                currentContract.RoomId,
                input.StartDate,
                input.EndDate,
                currentContract.Id
            );

            await EnsureNoConflictingReservedReservationAsync(
                currentContract.RoomId,
                input.EndDate
            );

            var renewedContract =
                new Contract(
                    GuidGenerator.Create(),
                    contractNumber,
                    currentContract.RoomId,
                    input.StartDate.Date,
                    input.EndDate?.Date,
                    input.MonthlyRent,
                    input.DepositAmount,
                    NormalizeOptional(input.Notes),
                    renewedFromContractId:
                        currentContract.Id
                );

            renewedContract.Sign();

            await _contractRepository.InsertAsync(
                renewedContract,
                autoSave: true
            );

            var currentTenants =
                await _contractTenantRepository.GetListAsync(
                    x =>
                        x.ContractId ==
                        currentContract.Id
                );

            foreach (var currentTenant in currentTenants)
            {
                var renewedTenant =
                    new ContractTenant(
                        GuidGenerator.Create(),
                        renewedContract.Id,
                        currentTenant.TenantId,
                        currentTenant.Role
                    );

                await _contractTenantRepository.InsertAsync(
                    renewedTenant,
                    autoSave: true
                );
            }

            await CancelConflictingPendingReservationsAsync(
                currentContract.RoomId,
                input.EndDate
            );

            renewalHold.Complete(renewedContract.Id);

            await _contractRenewalHoldRepository
                .UpdateAsync(
                    renewalHold,
                    autoSave: true
                );

            return ObjectMapper.Map<
                Contract,
                ContractDto
            >(renewedContract);
        }

        private async Task<ContractRenewalHold> GetValidRenewalHoldAsync(
        Guid currentContractId)
        {
            var hold =
                await _contractRenewalHoldRepository
                    .FirstOrDefaultAsync(
                        x =>
                            x.CurrentContractId ==
                            currentContractId
                    );

            if (hold is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractRenewalHoldRequired
                );
            }

            if (hold.Status ==
                ContractRenewalHoldStatus.Expired)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractRenewalHoldExpired
                );
            }

            if (hold.Status !=
                ContractRenewalHoldStatus.Active)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractRenewalHoldNotActive
                );
            }

            if (hold.ExpiresAt <= _clock.Now)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractRenewalHoldExpired
                );
            }

            return hold;
        }

        private async Task EnsureNoConflictingReservedReservationAsync(
        Guid roomId,
        DateTime? renewalEndDate)
        {
            bool exists;

            if (renewalEndDate.HasValue)
            {
                var endDate =
                    renewalEndDate.Value.Date;

                exists =
                    await _roomReservationRepository
                        .AnyAsync(
                            x =>
                                x.RoomId == roomId
                                &&
                                x.Status ==
                                    RoomReservationStatus.Reserved
                                &&
                                x.ExpectedMoveInDate <=
                                    endDate
                        );
            }
            else
            {
                exists =
                    await _roomReservationRepository
                        .AnyAsync(
                            x =>
                                x.RoomId == roomId
                                &&
                                x.Status ==
                                    RoomReservationStatus.Reserved
                        );
            }

            if (exists)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .RoomAlreadyReserved
                );
            }
        }

        private async Task CancelConflictingPendingReservationsAsync(
        Guid roomId,
        DateTime? renewalEndDate)
        {
            var queryable =
                await _roomReservationRepository
                    .GetQueryableAsync();

            var query =
                queryable.Where(
                    x =>
                        x.RoomId == roomId
                        &&
                        x.Status ==
                            RoomReservationStatus
                                .PendingPayment
                );

            if (renewalEndDate.HasValue)
            {
                var endDate =
                    renewalEndDate.Value.Date;

                query =
                    query.Where(
                        x =>
                            x.ExpectedMoveInDate <=
                                endDate
                    );
            }

            var reservations =
                await AsyncExecuter.ToListAsync(
                    query
                );

            if (reservations.Count == 0)
            {
                return;
            }

            var cancelledAt =
                _clock.Now;

            foreach (var reservation in reservations)
            {
                reservation.Cancel(
                    cancelledAt,
                    ReservationCancellationReason
                        .SupersededByRenewal
                );

                await _roomReservationRepository
                    .UpdateAsync(
                        reservation,
                        autoSave: true
                    );
            }
        }

        private async Task<bool> HasFutureCommittedOccupancyAsync(
        Guid roomId)
        {
            var hasReservedReservation =
                await _roomReservationRepository
                    .AnyAsync(
                        x =>
                            x.RoomId == roomId
                            &&
                            x.Status ==
                                RoomReservationStatus
                                    .Reserved
                    );

            if (hasReservedReservation)
            {
                return true;
            }

            var hasSignedContract =
                await _contractRepository
                    .AnyAsync(
                        x =>
                            x.RoomId == roomId
                            &&
                            x.Status ==
                                ContractStatus.Signed
                    );

            return hasSignedContract;
        }
    }
}