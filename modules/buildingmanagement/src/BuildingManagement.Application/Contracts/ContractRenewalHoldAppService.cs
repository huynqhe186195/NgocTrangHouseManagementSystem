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
    public class ContractRenewalHoldAppService
        : ApplicationService,
          IContractRenewalHoldAppService
    {
        private readonly IRepository<ContractRenewalHold, Guid>
            _renewalHoldRepository;

        private readonly IRepository<Contract, Guid>
            _contractRepository;

        private readonly IDataFilter<ISoftDelete>
            _softDeleteFilter;

        private readonly IClock
            _clock;

        public ContractRenewalHoldAppService(
            IRepository<ContractRenewalHold, Guid> renewalHoldRepository,
            IRepository<Contract, Guid> contractRepository,
            IDataFilter<ISoftDelete> softDeleteFilter,
            IClock clock)
        {
            _renewalHoldRepository =
                renewalHoldRepository;

            _contractRepository =
                contractRepository;

            _softDeleteFilter =
                softDeleteFilter;

            _clock =
                clock;
        }

        public async Task<ContractRenewalHoldDto>
            GetAsync(Guid id)
        {
            var hold =
                await GetExistingHoldAsync(id);

            await RefreshExpirationAsync(
                hold
            );

            return ObjectMapper.Map<
                ContractRenewalHold,
                ContractRenewalHoldDto
            >(hold);
        }

        public async Task<List<ContractRenewalHoldDto>>
            GetListAsync()
        {
            var queryable =
                await _renewalHoldRepository
                    .GetQueryableAsync();

            var holds =
                await AsyncExecuter.ToListAsync(
                    queryable.OrderByDescending(
                        x => x.RequestedAt
                    )
                );

            foreach (var hold in holds)
            {
                await RefreshExpirationAsync(
                    hold
                );
            }

            return ObjectMapper.Map<
                List<ContractRenewalHold>,
                List<ContractRenewalHoldDto>
            >(holds);
        }

        public async Task<ContractRenewalHoldDto>CreateAsync(CreateContractRenewalHoldDto input)
        {
            var currentContract =
                await _contractRepository.FindAsync(
                    input.CurrentContractId
                );

            if (currentContract is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractNotFound
                );
            }

            if (currentContract.Status !=
                ContractStatus.Active)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractRenewalHoldCannotBeCreated
                );
            }

            if (!currentContract.EndDate.HasValue)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractRenewalHoldCannotBeCreated
                );
            }

            await EnsureContractNotAlreadyRenewedAsync(
                currentContract.Id
            );

            await EnsureHoldDoesNotAlreadyExistAsync(
                currentContract.Id
            );

            var requestedAt =
                _clock.Now;

            var hold =
                new ContractRenewalHold(
                    GuidGenerator.Create(),
                    currentContract.Id,
                    currentContract.RoomId,
                    requestedAt,
                    NormalizeOptional(
                        input.Notes
                    )
                );

            await _renewalHoldRepository.InsertAsync(
                hold,
                autoSave: true
            );

            return ObjectMapper.Map<
                ContractRenewalHold,
                ContractRenewalHoldDto
            >(hold);
        }

        public async Task<ContractRenewalHoldDto>
            CancelAsync(Guid id)
        {
            var hold =
                await GetExistingHoldAsync(id);

            if (hold.Status !=
                ContractRenewalHoldStatus.Active)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractRenewalHoldCannotBeCancelled
                );
            }

            if (IsExpired(hold))
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractRenewalHoldExpired
                );
            }

            hold.Cancel();

            await _renewalHoldRepository.UpdateAsync(
                hold,
                autoSave: true
            );

            return ObjectMapper.Map<
                ContractRenewalHold,
                ContractRenewalHoldDto
            >(hold);
        }

        private async Task<ContractRenewalHold>
            GetExistingHoldAsync(Guid id)
        {
            var hold =
                await _renewalHoldRepository
                    .FindAsync(id);

            if (hold is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractRenewalHoldNotFound
                );
            }

            return hold;
        }

        private async Task
            EnsureHoldDoesNotAlreadyExistAsync(
                Guid contractId)
        {
            bool exists;

            using (_softDeleteFilter.Disable())
            {
                exists =
                    await _renewalHoldRepository
                        .AnyAsync(
                            x =>
                                x.CurrentContractId ==
                                    contractId
                        );
            }

            if (exists)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ContractRenewalHoldAlreadyExists
                );
            }
        }

        private async Task
            EnsureContractNotAlreadyRenewedAsync(
                Guid contractId)
        {
            bool exists;

            using (_softDeleteFilter.Disable())
            {
                exists =
                    await _contractRepository
                        .AnyAsync(
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

        private bool IsExpired(
            ContractRenewalHold hold)
        {
            return
                hold.Status ==
                    ContractRenewalHoldStatus.Active
                &&
                hold.ExpiresAt <= _clock.Now;
        }

        private async Task RefreshExpirationAsync(
            ContractRenewalHold hold)
        {
            if (!IsExpired(hold))
            {
                return;
            }

            hold.Expire();

            await _renewalHoldRepository.UpdateAsync(
                hold,
                autoSave: true
            );
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