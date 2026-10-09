using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;

namespace BuildingManagement.Utilities
{
    public class MeterReadingAppService
        : ApplicationService,
          IMeterReadingAppService
    {
        private readonly IRepository<MeterReading, Guid>
            _meterReadingRepository;

        private readonly IRepository<UtilityMeter, Guid>
            _utilityMeterRepository;

        public MeterReadingAppService(
            IRepository<MeterReading, Guid> meterReadingRepository,
            IRepository<UtilityMeter, Guid> utilityMeterRepository)
        {
            _meterReadingRepository =
                meterReadingRepository;

            _utilityMeterRepository =
                utilityMeterRepository;
        }

        public async Task<MeterReadingDto> GetAsync(
            Guid id)
        {
            var meterReading =
                await GetExistingMeterReadingAsync(id);

            return ObjectMapper.Map<
                MeterReading,
                MeterReadingDto
            >(meterReading);
        }

        public async Task<List<MeterReadingDto>>
            GetListAsync()
        {
            var queryable =
                await _meterReadingRepository
                    .GetQueryableAsync();

            var query =
                queryable
                    .OrderBy(x => x.UtilityMeterId)
                    .ThenBy(x => x.BillingYear)
                    .ThenBy(x => x.BillingMonth);

            var meterReadings =
                await AsyncExecuter.ToListAsync(query);

            return ObjectMapper.Map<
                List<MeterReading>,
                List<MeterReadingDto>
            >(meterReadings);
        }

        public async Task<MeterReadingDto> CreateAsync(
            CreateMeterReadingDto input)
        {
            ValidateBillingYear(
                input.BillingYear
            );

            ValidateBillingMonth(
                input.BillingMonth
            );

            var utilityMeter =
                await GetExistingUtilityMeterAsync(
                    input.UtilityMeterId
                );

            if (!utilityMeter.IsActive)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .UtilityMeterInactive
                );
            }

            await EnsurePeriodDoesNotExistAsync(
                input.UtilityMeterId,
                input.BillingYear,
                input.BillingMonth
            );

            var latestReading =
                await GetLatestReadingAsync(
                    input.UtilityMeterId
                );

            if (latestReading is not null &&
                !IsPeriodAfter(
                    input.BillingYear,
                    input.BillingMonth,
                    latestReading.BillingYear,
                    latestReading.BillingMonth))
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .MeterReadingPeriodMustBeAfterLatest
                );
            }

            var previousReading =
                latestReading?.CurrentReading
                ?? utilityMeter.InitialReading;

            ValidateCurrentReading(
                input.CurrentReading,
                previousReading
            );

            var readingDate =
                input.ReadingDate.Date;

            ValidateReadingDate(
                readingDate,
                utilityMeter.InstalledDate,
                latestReading
            );

            var meterReading =
                new MeterReading(
                    GuidGenerator.Create(),
                    input.UtilityMeterId,
                    input.BillingYear,
                    input.BillingMonth,
                    previousReading,
                    input.CurrentReading,
                    readingDate,
                    NormalizeOptional(
                        input.Notes
                    )
                );

            await _meterReadingRepository
                .InsertAsync(
                    meterReading,
                    autoSave: true
                );

            return ObjectMapper.Map<
                MeterReading,
                MeterReadingDto
            >(meterReading);
        }

        public async Task<MeterReadingDto> UpdateAsync(
            Guid id,
            UpdateMeterReadingDto input)
        {
            var meterReading =
                await GetExistingMeterReadingAsync(id);

            var latestReading =
                await GetLatestReadingAsync(
                    meterReading.UtilityMeterId
                );

            if (latestReading is null ||
                latestReading.Id != meterReading.Id)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .OnlyLatestMeterReadingCanBeUpdated
                );
            }

            var utilityMeter =
                await GetExistingUtilityMeterAsync(
                    meterReading.UtilityMeterId
                );

            ValidateCurrentReading(
                input.CurrentReading,
                meterReading.PreviousReading
            );

            var previousMeterReading =
                await GetPreviousReadingAsync(
                    meterReading
                );

            var readingDate =
                input.ReadingDate.Date;

            ValidateReadingDateForUpdate(
                readingDate,
                utilityMeter.InstalledDate,
                previousMeterReading
            );

            meterReading.Update(
                input.CurrentReading,
                readingDate,
                NormalizeOptional(
                    input.Notes
                )
            );

            await _meterReadingRepository
                .UpdateAsync(
                    meterReading,
                    autoSave: true
                );

            return ObjectMapper.Map<
                MeterReading,
                MeterReadingDto
            >(meterReading);
        }

        private async Task<MeterReading>
            GetExistingMeterReadingAsync(
                Guid id)
        {
            var meterReading =
                await _meterReadingRepository
                    .FindAsync(id);

            if (meterReading is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .MeterReadingNotFound
                );
            }

            return meterReading;
        }

        private async Task<UtilityMeter>
            GetExistingUtilityMeterAsync(
                Guid id)
        {
            var utilityMeter =
                await _utilityMeterRepository
                    .FindAsync(id);

            if (utilityMeter is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .UtilityMeterNotFound
                );
            }

            return utilityMeter;
        }

        private async Task
            EnsurePeriodDoesNotExistAsync(
                Guid utilityMeterId,
                int billingYear,
                int billingMonth)
        {
            var exists =
                await _meterReadingRepository
                    .AnyAsync(
                        x =>
                            x.UtilityMeterId ==
                                utilityMeterId
                            &&
                            x.BillingYear ==
                                billingYear
                            &&
                            x.BillingMonth ==
                                billingMonth
                    );

            if (exists)
            {
                throw new BusinessException(
                        BuildingManagementErrorCodes
                            .MeterReadingAlreadyExists
                    )
                    .WithData(
                        "BillingYear",
                        billingYear
                    )
                    .WithData(
                        "BillingMonth",
                        billingMonth
                    );
            }
        }

        private async Task<MeterReading?>
            GetLatestReadingAsync(
                Guid utilityMeterId)
        {
            var queryable =
                await _meterReadingRepository
                    .GetQueryableAsync();

            var query =
                queryable
                    .Where(
                        x =>
                            x.UtilityMeterId ==
                                utilityMeterId
                    )
                    .OrderByDescending(
                        x => x.BillingYear
                    )
                    .ThenByDescending(
                        x => x.BillingMonth
                    );

            return await AsyncExecuter
                .FirstOrDefaultAsync(query);
        }

        private async Task<MeterReading?>
            GetPreviousReadingAsync(
                MeterReading meterReading)
        {
            var queryable =
                await _meterReadingRepository
                    .GetQueryableAsync();

            var query =
                queryable
                    .Where(
                        x =>
                            x.UtilityMeterId ==
                                meterReading.UtilityMeterId
                            &&
                            (
                                x.BillingYear <
                                    meterReading.BillingYear
                                ||
                                (
                                    x.BillingYear ==
                                        meterReading.BillingYear
                                    &&
                                    x.BillingMonth <
                                        meterReading.BillingMonth
                                )
                            )
                    )
                    .OrderByDescending(
                        x => x.BillingYear
                    )
                    .ThenByDescending(
                        x => x.BillingMonth
                    );

            return await AsyncExecuter
                .FirstOrDefaultAsync(query);
        }

        private static bool IsPeriodAfter(
            int year,
            int month,
            int previousYear,
            int previousMonth)
        {
            if (year > previousYear)
            {
                return true;
            }

            if (year < previousYear)
            {
                return false;
            }

            return month > previousMonth;
        }

        private static void ValidateBillingYear(
            int billingYear)
        {
            if (billingYear < 1 ||
                billingYear > 9999)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidBillingYear
                );
            }
        }

        private static void ValidateBillingMonth(
            int billingMonth)
        {
            if (billingMonth < 1 ||
                billingMonth > 12)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidBillingMonth
                );
            }
        }

        private static void ValidateCurrentReading(
            decimal currentReading,
            decimal previousReading)
        {
            if (currentReading < previousReading)
            {
                throw new BusinessException(
                        BuildingManagementErrorCodes
                            .InvalidCurrentReading
                    )
                    .WithData(
                        "PreviousReading",
                        previousReading
                    );
            }
        }

        private static void ValidateReadingDate(
            DateTime readingDate,
            DateTime installedDate,
            MeterReading? latestReading)
        {
            if (readingDate < installedDate.Date)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidReadingDate
                );
            }

            if (latestReading is not null &&
                readingDate <=
                    latestReading.ReadingDate.Date)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidReadingDate
                );
            }
        }

        private static void
            ValidateReadingDateForUpdate(
                DateTime readingDate,
                DateTime installedDate,
                MeterReading? previousReading)
        {
            if (readingDate < installedDate.Date)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidReadingDate
                );
            }

            if (previousReading is not null &&
                readingDate <=
                    previousReading.ReadingDate.Date)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidReadingDate
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