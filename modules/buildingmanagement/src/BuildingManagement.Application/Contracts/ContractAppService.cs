using BuildingManagement.Rooms;
using BuildingManagement.Tenants;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;
using System.Linq;

namespace BuildingManagement.Contracts
{
    public class ContractAppService
        : ApplicationService,
          IContractAppService
    {
        private readonly IRepository<Contract, Guid>
            _contractRepository;

        private readonly IRepository<ContractTenant, Guid>
            _contractTenantRepository;

        private readonly IRepository<Room, Guid>
            _roomRepository;

        private readonly IRepository<Tenant, Guid>
            _tenantRepository;

        private readonly IDataFilter<ISoftDelete>
            _softDeleteFilter;

        public ContractAppService(
            IRepository<Contract, Guid> contractRepository,
            IRepository<ContractTenant, Guid> contractTenantRepository,
            IRepository<Room, Guid> roomRepository,
            IRepository<Tenant, Guid> tenantRepository,
            IDataFilter<ISoftDelete> softDeleteFilter)
        {
            _contractRepository = contractRepository;
            _contractTenantRepository =
                contractTenantRepository;
            _roomRepository = roomRepository;
            _tenantRepository = tenantRepository;
            _softDeleteFilter = softDeleteFilter;
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

            if (contract.Status != ContractStatus.Draft)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractCannotBeActivated
                );
            }

            var contractTenants =
                await _contractTenantRepository.GetListAsync(
                    x => x.ContractId == contract.Id
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
                    x => x.Role ==
                         ContractTenantRole.PrimaryTenant
                );

            if (primaryTenantCount != 1)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractRequiresPrimaryTenant
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
                room.ChangeStatus(
                    RoomStatus.Available
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

            if (contract.Status != ContractStatus.Draft)
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
    }
}