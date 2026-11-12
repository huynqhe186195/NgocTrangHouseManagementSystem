using BuildingManagement.Rooms;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Uow;

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

        private readonly IRepository<MeterReading, Guid>
            _meterReadingRepository;

        private readonly IRepository<MeterReplacement, Guid>
            _meterReplacementRepository;

        private readonly IUtilityMeterLock _meterLock;

        public UtilityMeterAppService(
    IRepository<UtilityMeter, Guid> utilityMeterRepository,
    IRepository<Room, Guid> roomRepository,
    IRepository<MeterReading, Guid> meterReadingRepository,
    IRepository<MeterReplacement, Guid> meterReplacementRepository,
    IUtilityMeterLock meterLock,
    IDataFilter<ISoftDelete> softDeleteFilter)
        {
            _utilityMeterRepository =
                utilityMeterRepository;

            _roomRepository =
                roomRepository;

            _meterReadingRepository =
                meterReadingRepository;

            _meterReplacementRepository = meterReplacementRepository;
            _meterLock = meterLock;

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

        [UnitOfWork(isTransactional: true)]
        public virtual async Task<UtilityMeterDto> UpdateAsync(
            Guid id,
            UpdateUtilityMeterDto input)
        {
            await _meterLock.AcquireAsync(id);
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

            // A meter with recorded consumption must retain its physical identity
            // and the opening reference point. Corrections require a dedicated
            // auditable workflow instead of rewriting existing history.
            await EnsureMeterIdentityAndOpeningReadingNotChangedAsync(
                utilityMeter,
                meterCode,
                input.InitialReading,
                input.InstalledDate.Date
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

        [UnitOfWork(isTransactional: true)]
        public virtual async Task<UtilityMeterDto>
            DeactivateAsync(
                Guid id)
        {
            await _meterLock.AcquireAsync(id);
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

        /// <summary>
        /// Complete replacement in ONE ABP UOW. Never call Deactivate + Create
        /// separately; those calls cannot preserve a closing-reading snapshot.
        /// </summary>
        [UnitOfWork(isTransactional: true)]
        public virtual async Task<MeterReplacementDto> ReplaceAsync(
            Guid oldMeterId, ReplaceUtilityMeterDto input)
        {
            if (input is null) throw new ArgumentNullException(nameof(input));
            await _meterLock.AcquireAsync(oldMeterId);
            var oldMeter = await GetExistingUtilityMeterAsync(oldMeterId);
            if (!oldMeter.IsActive)
                throw new BusinessException(MeterReplacementErrorCodes.OldMeterMustBeActive);

            var replacementDate = input.ReplacementDate.Date;
            if (replacementDate == default ||
                replacementDate < oldMeter.InstalledDate.Date)
                throw new BusinessException(MeterReplacementErrorCodes.InvalidReplacementDate);

            var newMeterCode = NormalizeMeterCode(input.NewMeterCode);
            ValidateMeterCode(newMeterCode);
            if (input.NewInitialReading < 0)
                throw new BusinessException(MeterReplacementErrorCodes.InvalidNewInitialReading);
            if (input.OldFinalReading < 0)
                throw new BusinessException(MeterReplacementErrorCodes.InvalidClosingReading);

            if (string.IsNullOrWhiteSpace(input.Reason))
                throw new BusinessException(MeterReplacementErrorCodes.ReasonRequired);
            var reason = input.Reason.Trim();
            var notes = NormalizeOptional(input.Notes);
            if (reason.Length > 500 || (notes?.Length ?? 0) > 500)
                throw new BusinessException(MeterReplacementErrorCodes.InvalidReasonOrNotes);

            await EnsureMeterCodeUniqueAsync(newMeterCode);
            // A meter may be the NEW side of an earlier replacement and
            // subsequently become OLD in another. Only duplicate outgoing
            // replacement is forbidden; this preserves multi-hop history.
            if (await _meterReplacementRepository.AnyAsync(x => x.OldMeterId == oldMeterId))
                throw new BusinessException(MeterReplacementErrorCodes.MeterReferencedByReplacement);

            // A soft-deleted reading must NEVER disappear from the closing basis.
            // Reject it pending manual review instead of guessing the old index.
            bool hasDeletedReading;
            using (_softDeleteFilter.Disable())
            {
                hasDeletedReading = await _meterReadingRepository.AnyAsync(
                    x => x.UtilityMeterId == oldMeterId && x.IsDeleted);
            }
            if (hasDeletedReading)
                throw new BusinessException(
                    MeterReplacementErrorCodes.SoftDeletedHistoryRequiresReview);

            var readings = await _meterReadingRepository.GetQueryableAsync();
            var latest = await AsyncExecuter.FirstOrDefaultAsync(
                readings.Where(x => x.UtilityMeterId == oldMeterId)
                    .OrderByDescending(x => x.BillingYear)
                    .ThenByDescending(x => x.BillingMonth));

            if (latest is not null && replacementDate < latest.ReadingDate.Date)
                throw new BusinessException(MeterReplacementErrorCodes.InvalidReplacementDate);

            var previous = latest?.CurrentReading ?? oldMeter.InitialReading;
            if (input.OldFinalReading < previous)
                throw new BusinessException(MeterReplacementErrorCodes.InvalidClosingReading)
                    .WithData("PreviousReading", previous);

            // Keep the historical meter untouched except for IsActive.
            oldMeter.Deactivate();
            await _utilityMeterRepository.UpdateAsync(oldMeter, autoSave: true);

            var newMeter = new UtilityMeter(
                GuidGenerator.Create(), oldMeter.RoomId, oldMeter.UtilityType,
                newMeterCode, input.NewInitialReading, replacementDate, notes);
            await _utilityMeterRepository.InsertAsync(newMeter, autoSave: true);

            var record = new MeterReplacement(
                GuidGenerator.Create(), oldMeter.Id, newMeter.Id, oldMeter.RoomId,
                oldMeter.UtilityType, replacementDate, previous,
                input.OldFinalReading, input.NewInitialReading, reason, notes);
            await _meterReplacementRepository.InsertAsync(record, autoSave: true);
            return ToReplacementDto(record);
        }

        public async Task<MeterReplacementDto> GetReplacementAsync(Guid id)
        {
            var record = await _meterReplacementRepository.FindAsync(id);
            if (record is null)
                throw new BusinessException(MeterReplacementErrorCodes.ReplacementNotFound);
            return ToReplacementDto(record);
        }

        public async Task<List<MeterReplacementDto>> GetReplacementsByRoomAsync(Guid roomId)
        {
            await EnsureRoomExistsAsync(roomId);
            var query = await _meterReplacementRepository.GetQueryableAsync();
            var list = await AsyncExecuter.ToListAsync(
                query.Where(x => x.RoomId == roomId)
                    .OrderBy(x => x.ReplacementDate).ThenBy(x => x.CreationTime));
            return list.Select(ToReplacementDto).ToList();
        }

        private async Task<bool> IsReferencedByReplacementAsync(Guid meterId)
        {
            return await _meterReplacementRepository.AnyAsync(
                x => x.OldMeterId == meterId || x.NewMeterId == meterId);
        }

        private static MeterReplacementDto ToReplacementDto(MeterReplacement record) => new()
        {
            Id = record.Id,
            OldMeterId = record.OldMeterId,
            NewMeterId = record.NewMeterId,
            RoomId = record.RoomId,
            UtilityType = record.UtilityType,
            ReplacementDate = record.ReplacementDate,
            OldPreviousReading = record.OldPreviousReading,
            OldFinalReading = record.OldFinalReading,
            NewInitialReading = record.NewInitialReading,
            Reason = record.Reason,
            Notes = record.Notes,
            CreationTime = record.CreationTime
        };

        [UnitOfWork(isTransactional: true)]
        public virtual async Task DeleteAsync(Guid id)
        {
            await _meterLock.AcquireAsync(id);
            var utilityMeter =
                await GetExistingUtilityMeterAsync(id);

            bool hasReadingHistory;

            using (_softDeleteFilter.Disable())
            {
                hasReadingHistory =
                    await _meterReadingRepository
                        .AnyAsync(
                            x =>
                                x.UtilityMeterId ==
                                    utilityMeter.Id
                        );
            }

            if (hasReadingHistory)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .UtilityMeterHasReadingHistory
                );
            }

            if (await IsReferencedByReplacementAsync(id))
                throw new BusinessException(MeterReplacementErrorCodes.MeterReferencedByReplacement);

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

        private async Task EnsureMeterIdentityAndOpeningReadingNotChangedAsync(
            UtilityMeter utilityMeter,
            string meterCode,
            decimal initialReading,
            DateTime installedDate)
        {
            if (utilityMeter.MeterCode == meterCode &&
                utilityMeter.InitialReading == initialReading &&
                utilityMeter.InstalledDate.Date == installedDate)
            {
                return;
            }

            // Even a meter without monthly readings cannot have its physical
            // identity/initial reading changed after a replacement record exists.
            if (await IsReferencedByReplacementAsync(utilityMeter.Id))
                throw new BusinessException(
                    MeterReplacementErrorCodes.MeterIdentityLockedByReplacement);

            // A soft-deleted reading is still part of the historical record.
            // Disable the filter to avoid allowing accidental history rewrites.
            bool hasReadingHistory;
            using (_softDeleteFilter.Disable())
            {
                hasReadingHistory = await _meterReadingRepository.AnyAsync(
                    reading => reading.UtilityMeterId == utilityMeter.Id
                );
            }

            if (hasReadingHistory)
            {
                throw new BusinessException(
                    UtilityDataIntegrityErrorCodes.MeterIdentityLockedByReadings
                );
            }
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