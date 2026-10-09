using BuildingManagement.Buildings;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;

namespace BuildingManagement.Utilities
{
    public class UtilityRateAppService
        : ApplicationService,
          IUtilityRateAppService
    {
        private readonly IRepository<UtilityRate, Guid>
            _utilityRateRepository;

        private readonly IRepository<Building, Guid>
            _buildingRepository;

        private readonly IDataFilter<ISoftDelete>
            _softDeleteFilter;

        public UtilityRateAppService(
            IRepository<UtilityRate, Guid> utilityRateRepository,
            IRepository<Building, Guid> buildingRepository,
            IDataFilter<ISoftDelete> softDeleteFilter)
        {
            _utilityRateRepository = utilityRateRepository;
            _buildingRepository = buildingRepository;
            _softDeleteFilter = softDeleteFilter;
        }

        public async Task<UtilityRateDto> GetAsync(
            Guid id)
        {
            var utilityRate =
                await GetExistingUtilityRateAsync(id);

            return ObjectMapper.Map<
                UtilityRate,
                UtilityRateDto
            >(utilityRate);
        }

        public async Task<List<UtilityRateDto>>
            GetListAsync()
        {
            var utilityRates =
                await _utilityRateRepository.GetListAsync();

            return ObjectMapper.Map<
                List<UtilityRate>,
                List<UtilityRateDto>
            >(utilityRates);
        }

        public async Task<UtilityRateDto> CreateAsync(
            CreateUtilityRateDto input)
        {
            var effectiveFrom =
                input.EffectiveFrom.Date;

            var effectiveTo =
                input.EffectiveTo?.Date;

            ValidateUtilityRateValues(
                input.UtilityType,
                input.UnitPrice,
                effectiveFrom,
                effectiveTo
            );

            await EnsureBuildingExistsAsync(
                input.BuildingId
            );

            await EnsureNoOverlappingRateAsync(
                input.BuildingId,
                input.UtilityType,
                effectiveFrom,
                effectiveTo
            );

            var utilityRate =
                new UtilityRate(
                    GuidGenerator.Create(),
                    input.BuildingId,
                    input.UtilityType,
                    input.UnitPrice,
                    effectiveFrom,
                    effectiveTo,
                    NormalizeOptional(input.Notes)
                );

            await _utilityRateRepository.InsertAsync(
                utilityRate,
                autoSave: true
            );

            return ObjectMapper.Map<
                UtilityRate,
                UtilityRateDto
            >(utilityRate);
        }

        public async Task<UtilityRateDto> UpdateAsync(
            Guid id,
            UpdateUtilityRateDto input)
        {
            var utilityRate =
                await GetExistingUtilityRateAsync(id);

            var effectiveFrom =
                input.EffectiveFrom.Date;

            var effectiveTo =
                input.EffectiveTo?.Date;

            ValidateUtilityRateValues(
                input.UtilityType,
                input.UnitPrice,
                effectiveFrom,
                effectiveTo
            );

            await EnsureBuildingExistsAsync(
                input.BuildingId
            );

            await EnsureNoOverlappingRateAsync(
                input.BuildingId,
                input.UtilityType,
                effectiveFrom,
                effectiveTo,
                utilityRate.Id
            );

            utilityRate.Update(
                input.BuildingId,
                input.UtilityType,
                input.UnitPrice,
                effectiveFrom,
                effectiveTo,
                NormalizeOptional(input.Notes)
            );

            await _utilityRateRepository.UpdateAsync(
                utilityRate,
                autoSave: true
            );

            return ObjectMapper.Map<
                UtilityRate,
                UtilityRateDto
            >(utilityRate);
        }

        public async Task DeleteAsync(Guid id)
        {
            var utilityRate =
                await GetExistingUtilityRateAsync(id);

            await _utilityRateRepository.DeleteAsync(
                utilityRate,
                autoSave: true
            );
        }

        private async Task<UtilityRate>
            GetExistingUtilityRateAsync(
                Guid id)
        {
            var utilityRate =
                await _utilityRateRepository.FindAsync(id);

            if (utilityRate is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .UtilityRateNotFound
                );
            }

            return utilityRate;
        }

        private async Task EnsureBuildingExistsAsync(
            Guid buildingId)
        {
            var exists =
                await _buildingRepository.AnyAsync(
                    x => x.Id == buildingId
                );

            if (!exists)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .BuildingNotFound
                );
            }
        }

        private async Task EnsureNoOverlappingRateAsync(
            Guid buildingId,
            UtilityType utilityType,
            DateTime effectiveFrom,
            DateTime? effectiveTo,
            Guid? excludedUtilityRateId = null)
        {
            bool overlaps;

            using (_softDeleteFilter.Disable())
            {
                overlaps =
                    await _utilityRateRepository.AnyAsync(
                        x =>
                            x.BuildingId == buildingId
                            &&
                            x.UtilityType == utilityType
                            &&
                            (
                                !excludedUtilityRateId.HasValue
                                ||
                                x.Id !=
                                    excludedUtilityRateId.Value
                            )
                            &&
                            (
                                x.EffectiveTo == null
                                ||
                                x.EffectiveTo.Value >=
                                    effectiveFrom
                            )
                            &&
                            (
                                effectiveTo == null
                                ||
                                x.EffectiveFrom <=
                                    effectiveTo.Value
                            )
                    );
            }

            if (overlaps)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .UtilityRatePeriodOverlaps
                );
            }
        }

        private static void ValidateUtilityRateValues(
            UtilityType utilityType,
            decimal unitPrice,
            DateTime effectiveFrom,
            DateTime? effectiveTo)
        {
            if (!Enum.IsDefined(
                    typeof(UtilityType),
                    utilityType))
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidUtilityType
                );
            }

            if (unitPrice <= 0)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidUtilityUnitPrice
                );
            }

            if (effectiveTo.HasValue &&
                effectiveTo.Value <
                effectiveFrom)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidUtilityRatePeriod
                );
            }
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