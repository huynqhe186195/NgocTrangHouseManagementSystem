using BuildingManagement.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories;

namespace BuildingManagement.Tenants
{
    public class TenantAppService
        : ApplicationService,
          ITenantAppService
    {
        private readonly IRepository<Tenant, Guid>
            _tenantRepository;

        private readonly IDataFilter<ISoftDelete>
            _softDeleteFilter;

        private readonly IRepository<Contract, Guid>
            _contractRepository;

        private readonly IRepository<ContractTenant, Guid>
            _contractTenantRepository;

        public TenantAppService(
            IRepository<Tenant, Guid> tenantRepository,
            IDataFilter<ISoftDelete> softDeleteFilter,
            IRepository<Contract, Guid> contractRepository,
            IRepository<ContractTenant, Guid> contractTenantRepository)
        {
            _tenantRepository =
                tenantRepository;

            _softDeleteFilter =
                softDeleteFilter;

            _contractRepository =
                contractRepository;

            _contractTenantRepository =
                contractTenantRepository;
        }

        public async Task<TenantDto> GetAsync(
            Guid id)
        {
            var tenant =
                await GetExistingTenantAsync(id);

            return ObjectMapper.Map<
                Tenant,
                TenantDto
            >(tenant);
        }

        public async Task<List<TenantDto>>
            GetListAsync()
        {
            var tenants =
                await _tenantRepository
                    .GetListAsync();

            return ObjectMapper.Map<
                List<Tenant>,
                List<TenantDto>
            >(tenants);
        }

        public async Task<TenantDto> CreateAsync(
            CreateTenantDto input)
        {
            var phoneNumber =
                NormalizePhoneNumber(
                    input.PhoneNumber
                );

            var identityNumber =
                NormalizeCitizenId(
                    input.IdentityNumber
                );

            ValidateCitizenIdentityType(
                input.IdentityType
            );

            await EnsurePhoneNumberUniqueAsync(
                phoneNumber
            );

            await EnsureIdentityUniqueAsync(
                IdentityType.CitizenId,
                identityNumber
            );

            var tenant =
                new Tenant(
                    GuidGenerator.Create(),
                    input.FullName.Trim(),
                    phoneNumber,
                    NormalizeOptional(
                        input.Email
                    ),
                    input.DateOfBirth,
                    input.Gender,
                    IdentityType.CitizenId,
                    identityNumber,
                    NormalizeOptional(
                        input.PermanentAddress
                    ),
                    NormalizeOptional(
                        input.EmergencyContactName
                    ),
                    NormalizeOptional(
                        input.EmergencyContactPhone
                    ),
                    NormalizeOptional(
                        input.Notes
                    )
                );

            await _tenantRepository
                .InsertAsync(
                    tenant,
                    autoSave: true
                );

            return ObjectMapper.Map<
                Tenant,
                TenantDto
            >(tenant);
        }

        public async Task<TenantDto> UpdateAsync(
            Guid id,
            UpdateTenantDto input)
        {
            var tenant =
                await GetExistingTenantAsync(id);

            var phoneNumber =
                NormalizePhoneNumber(
                    input.PhoneNumber
                );

            var identityNumber =
                NormalizeCitizenId(
                    input.IdentityNumber
                );

            ValidateCitizenIdentityType(
                input.IdentityType
            );

            await EnsurePhoneNumberUniqueAsync(
                phoneNumber,
                tenant.Id
            );

            await EnsureIdentityUniqueAsync(
                IdentityType.CitizenId,
                identityNumber,
                tenant.Id
            );

            tenant.Update(
                input.FullName.Trim(),
                phoneNumber,
                NormalizeOptional(
                    input.Email
                ),
                input.DateOfBirth,
                input.Gender,
                IdentityType.CitizenId,
                identityNumber,
                NormalizeOptional(
                    input.PermanentAddress
                ),
                NormalizeOptional(
                    input.EmergencyContactName
                ),
                NormalizeOptional(
                    input.EmergencyContactPhone
                ),
                NormalizeOptional(
                    input.Notes
                )
            );

            await _tenantRepository
                .UpdateAsync(
                    tenant,
                    autoSave: true
                );

            return ObjectMapper.Map<
                Tenant,
                TenantDto
            >(tenant);
        }

        public async Task DeleteAsync(
            Guid id)
        {
            var tenant =
                await GetExistingTenantAsync(id);

            var contractTenants =
                await _contractTenantRepository
                    .GetListAsync(
                        x => x.TenantId == id
                    );

            if (contractTenants.Count > 0)
            {
                var contractIds =
                    contractTenants
                        .Select(
                            x => x.ContractId
                        )
                        .ToList();

                var hasActiveContract =
                    await _contractRepository
                        .AnyAsync(
                            x =>
                                contractIds.Contains(
                                    x.Id
                                )
                                &&
                                x.Status ==
                                    ContractStatus.Active
                        );

                if (hasActiveContract)
                {
                    throw new BusinessException(
                        BuildingManagementErrorCodes
                            .TenantHasActiveContract
                    );
                }

                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .TenantHasContractHistory
                );
            }

            await _tenantRepository
                .DeleteAsync(
                    tenant,
                    autoSave: true
                );
        }

        private async Task<Tenant>
            GetExistingTenantAsync(
                Guid id)
        {
            var tenant =
                await _tenantRepository
                    .FindAsync(id);

            if (tenant is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .TenantNotFound
                );
            }

            return tenant;
        }

        private async Task
            EnsurePhoneNumberUniqueAsync(
                string phoneNumber,
                Guid? excludedTenantId = null)
        {
            bool exists;

            using (_softDeleteFilter.Disable())
            {
                exists =
                    await _tenantRepository
                        .AnyAsync(
                            tenant =>
                                tenant.PhoneNumber ==
                                    phoneNumber
                                &&
                                (
                                    !excludedTenantId
                                        .HasValue
                                    ||
                                    tenant.Id !=
                                        excludedTenantId
                                            .Value
                                )
                        );
            }

            if (exists)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .TenantPhoneNumberAlreadyExists
                )
                .WithData(
                    "PhoneNumber",
                    phoneNumber
                );
            }
        }

        private async Task
            EnsureIdentityUniqueAsync(
                IdentityType identityType,
                string identityNumber,
                Guid? excludedTenantId = null)
        {
            bool exists;

            using (_softDeleteFilter.Disable())
            {
                exists =
                    await _tenantRepository
                        .AnyAsync(
                            tenant =>
                                tenant.IdentityType ==
                                    identityType
                                &&
                                tenant.IdentityNumber ==
                                    identityNumber
                                &&
                                (
                                    !excludedTenantId
                                        .HasValue
                                    ||
                                    tenant.Id !=
                                        excludedTenantId
                                            .Value
                                )
                        );
            }

            if (exists)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .TenantIdentityAlreadyExists
                )
                .WithData(
                    "IdentityNumber",
                    identityNumber
                );
            }
        }

        private static void
            ValidateCitizenIdentityType(
                IdentityType? identityType)
        {
            if (
                !identityType.HasValue
                ||
                identityType.Value !=
                    IdentityType.CitizenId
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .TenantIdentityMustBeCitizenId
                );
            }
        }

        private static string
            NormalizePhoneNumber(
                string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidTenantPhoneNumber
                );
            }

            var normalized =
                value
                    .Trim()
                    .Replace(
                        " ",
                        string.Empty
                    )
                    .Replace(
                        "-",
                        string.Empty
                    )
                    .Replace(
                        ".",
                        string.Empty
                    );

            if (
                normalized.StartsWith(
                    "+84"
                )
            )
            {
                normalized =
                    "0" + normalized[3..];
            }
            else if (
                normalized.StartsWith(
                    "84"
                )
                &&
                normalized.Length == 11
            )
            {
                normalized =
                    "0" + normalized[2..];
            }

            if (
                normalized.Length != 10
                ||
                normalized[0] != '0'
                ||
                !normalized.All(
                    char.IsDigit
                )
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidTenantPhoneNumber
                );
            }

            return normalized;
        }

        private static string
            NormalizeCitizenId(
                string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidTenantCitizenId
                );
            }

            var normalized =
                value.Trim();

            if (
                normalized.Length != 12
                ||
                !normalized.All(
                    char.IsDigit
                )
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidTenantCitizenId
                );
            }

            return normalized;
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
    }
}