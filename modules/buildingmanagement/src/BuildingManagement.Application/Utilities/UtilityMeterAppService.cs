using BuildingManagement.Rooms;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;

namespace BuildingManagement.Utilities
{
    public class UtilityMeterAppService
        : ApplicationService,
          IUtilityMeterAppService
    {
        private readonly IRepository<UtilityMeter, Guid>
            _utilityMeterRepository;

        private readonly IRepository<Room, Guid>
            _roomRepository;

        private readonly IDataFilter<ISoftDelete>
            _softDeleteFilter;

        public UtilityMeterAppService(
            IRepository<UtilityMeter, Guid> utilityMeterRepository,
            IRepository<Room, Guid> roomRepository,
            IDataFilter<ISoftDelete> softDeleteFilter)
        {
            _utilityMeterRepository =
                utilityMeterRepository;

            _roomRepository =
                roomRepository;

            _softDeleteFilter =
                softDeleteFilter;
        }

        public async Task<UtilityMeterDto> GetAsync(
            Guid id)
        {
            var utilityMeter =
                await GetExistingUtilityMeterAsync(id);

            return ObjectMapper.Map<
                UtilityMeter,
                UtilityMeterDto
            >(utilityMeter);
        }

        public async Task<List<UtilityMeterDto>>
            GetListAsync()
        {
            var utilityMeters =
                await _utilityMeterRepository
                    .GetListAsync();

            return ObjectMapper.Map<
                List<UtilityMeter>,
                List<UtilityMeterDto>
            >(utilityMeters);
        }

        public async Task<UtilityMeterDto> CreateAsync(
            CreateUtilityMeterDto input)
        {
            ValidateUtilityType(
                input.UtilityType
            );

            ValidateInitialReading(
                input.InitialReading
            );

            var meterCode =
                NormalizeMeterCode(
                    input.MeterCode
                );

            ValidateMeterCode(
                meterCode
            );

            await EnsureRoomExistsAsync(
                input.RoomId
            );

            await EnsureMeterCodeUniqueAsync(
                meterCode
            );

            await EnsureNoActiveMeterAsync(
                input.RoomId,
                input.UtilityType
            );

            var utilityMeter =
                new UtilityMeter(
                    GuidGenerator.Create(),
                    input.RoomId,
                    input.UtilityType,
                    meterCode,
                    input.InitialReading,
                    input.InstalledDate.Date,
                    NormalizeOptional(
                        input.Notes
                    )
                );

            await _utilityMeterRepository
                .InsertAsync(
                    utilityMeter,
                    autoSave: true
                );

            return ObjectMapper.Map<
                UtilityMeter,
                UtilityMeterDto
            >(utilityMeter);
        }

        public async Task<UtilityMeterDto> UpdateAsync(
            Guid id,
            UpdateUtilityMeterDto input)
        {
            var utilityMeter =
                await GetExistingUtilityMeterAsync(id);

            ValidateInitialReading(
                input.InitialReading
            );

            var meterCode =
                NormalizeMeterCode(
                    input.MeterCode
                );

            ValidateMeterCode(
                meterCode
            );

            await EnsureMeterCodeUniqueAsync(
                meterCode,
                utilityMeter.Id
            );

            utilityMeter.Update(
                meterCode,
                input.InitialReading,
                input.InstalledDate.Date,
                NormalizeOptional(
                    input.Notes
                )
            );

            await _utilityMeterRepository
                .UpdateAsync(
                    utilityMeter,
                    autoSave: true
                );

            return ObjectMapper.Map<
                UtilityMeter,
                UtilityMeterDto
            >(utilityMeter);
        }

        public async Task<UtilityMeterDto>
            DeactivateAsync(
                Guid id)
        {
            var utilityMeter =
                await GetExistingUtilityMeterAsync(id);

            if (!utilityMeter.IsActive)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .UtilityMeterAlreadyInactive
                );
            }

            utilityMeter.Deactivate();

            await _utilityMeterRepository
                .UpdateAsync(
                    utilityMeter,
                    autoSave: true
                );

            return ObjectMapper.Map<
                UtilityMeter,
                UtilityMeterDto
            >(utilityMeter);
        }

        public async Task DeleteAsync(Guid id)
        {
            var utilityMeter =
                await GetExistingUtilityMeterAsync(id);

            await _utilityMeterRepository
                .DeleteAsync(
                    utilityMeter,
                    autoSave: true
                );
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

        private async Task
            EnsureMeterCodeUniqueAsync(
                string meterCode,
                Guid? excludedUtilityMeterId = null)
        {
            bool exists;

            using (_softDeleteFilter.Disable())
            {
                exists =
                    await _utilityMeterRepository
                        .AnyAsync(
                            x =>
                                x.MeterCode ==
                                    meterCode
                                &&
                                (
                                    !excludedUtilityMeterId
                                        .HasValue
                                    ||
                                    x.Id !=
                                        excludedUtilityMeterId
                                            .Value
                                )
                        );
            }

            if (exists)
            {
                throw new BusinessException(
                        BuildingManagementErrorCodes
                            .MeterCodeAlreadyExists
                    )
                    .WithData(
                        "MeterCode",
                        meterCode
                    );
            }
        }

        private async Task
            EnsureNoActiveMeterAsync(
                Guid roomId,
                UtilityType utilityType)
        {
            var exists =
                await _utilityMeterRepository
                    .AnyAsync(
                        x =>
                            x.RoomId == roomId
                            &&
                            x.UtilityType ==
                                utilityType
                            &&
                            x.IsActive
                    );

            if (exists)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .ActiveUtilityMeterAlreadyExists
                );
            }
        }

        private static void ValidateUtilityType(
            UtilityType utilityType)
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
        }

        private static void ValidateInitialReading(
            decimal initialReading)
        {
            if (initialReading < 0)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidInitialMeterReading
                );
            }
        }

        private static void ValidateMeterCode(
            string meterCode)
        {
            if (string.IsNullOrWhiteSpace(
                    meterCode)
                ||
                meterCode.Length > 64)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidMeterCode
                );
            }
        }

        private static string NormalizeMeterCode(
            string? value)
        {
            return value?
                       .Trim()
                       .ToUpperInvariant()
                   ?? string.Empty;
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