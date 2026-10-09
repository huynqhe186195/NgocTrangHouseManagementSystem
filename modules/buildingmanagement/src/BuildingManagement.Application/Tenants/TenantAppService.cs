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
        private readonly IRepository<Tenant, Guid> _tenantRepository;
        private readonly IDataFilter<ISoftDelete> _softDeleteFilter;

        public TenantAppService(
    IRepository<Tenant, Guid> tenantRepository,
    IDataFilter<ISoftDelete> softDeleteFilter)
        {
            _tenantRepository = tenantRepository;
            _softDeleteFilter = softDeleteFilter;
        }

        public async Task<TenantDto> GetAsync(Guid id)
        {
            var tenant =
                await GetExistingTenantAsync(id);

            return ObjectMapper.Map<Tenant, TenantDto>(
                tenant
            );
        }

        public async Task<List<TenantDto>> GetListAsync()
        {
            var tenants =
                await _tenantRepository.GetListAsync();

            return ObjectMapper.Map<
                List<Tenant>,
                List<TenantDto>
            >(tenants);
        }

        public async Task<TenantDto> CreateAsync(
            CreateTenantDto input)
        {
            var identityNumber =
                NormalizeIdentityNumber(
                    input.IdentityNumber
                );

            ValidateIdentityInformation(
                input.IdentityType,
                identityNumber
            );

            await EnsureIdentityUniqueAsync(
                input.IdentityType,
                identityNumber
            );

            var tenant = new Tenant(
                GuidGenerator.Create(),
                input.FullName.Trim(),
                NormalizeOptional(input.PhoneNumber),
                NormalizeOptional(input.Email),
                input.DateOfBirth,
                input.Gender,
                input.IdentityType,
                identityNumber,
                NormalizeOptional(input.PermanentAddress),
                NormalizeOptional(input.EmergencyContactName),
                NormalizeOptional(input.EmergencyContactPhone),
                NormalizeOptional(input.Notes)
            );

            await _tenantRepository.InsertAsync(
                tenant,
                autoSave: true
            );

            return ObjectMapper.Map<Tenant, TenantDto>(
                tenant
            );
        }

        public async Task<TenantDto> UpdateAsync(
            Guid id,
            UpdateTenantDto input)
        {
            var tenant =
                await GetExistingTenantAsync(id);

            var identityNumber =
                NormalizeIdentityNumber(
                    input.IdentityNumber
                );

            ValidateIdentityInformation(
                input.IdentityType,
                identityNumber
            );

            await EnsureIdentityUniqueAsync(
                input.IdentityType,
                identityNumber,
                tenant.Id
            );

            tenant.Update(
                input.FullName.Trim(),
                NormalizeOptional(input.PhoneNumber),
                NormalizeOptional(input.Email),
                input.DateOfBirth,
                input.Gender,
                input.IdentityType,
                identityNumber,
                NormalizeOptional(input.PermanentAddress),
                NormalizeOptional(input.EmergencyContactName),
                NormalizeOptional(input.EmergencyContactPhone),
                NormalizeOptional(input.Notes)
            );

            await _tenantRepository.UpdateAsync(
                tenant,
                autoSave: true
            );

            return ObjectMapper.Map<Tenant, TenantDto>(
                tenant
            );
        }

        public async Task DeleteAsync(Guid id)
        {
            var tenant =
                await GetExistingTenantAsync(id);

            await _tenantRepository.DeleteAsync(
                tenant,
                autoSave: true
            );
        }

        private async Task<Tenant> GetExistingTenantAsync(
            Guid id)
        {
            var tenant =
                await _tenantRepository.FindAsync(id);

            if (tenant is null)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .TenantNotFound
                );
            }

            return tenant;
        }

        private async Task EnsureIdentityUniqueAsync(
    IdentityType? identityType,
    string? identityNumber,
    Guid? excludedTenantId = null)
        {
            if (!identityType.HasValue ||
                string.IsNullOrWhiteSpace(identityNumber))
            {
                return;
            }

            bool exists;

            using (_softDeleteFilter.Disable())
            {
                exists = await _tenantRepository.AnyAsync(
                    tenant =>
                        tenant.IdentityType == identityType
                        && tenant.IdentityNumber == identityNumber
                        && (!excludedTenantId.HasValue
                            || tenant.Id != excludedTenantId.Value)
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

        private static void ValidateIdentityInformation(
            IdentityType? identityType,
            string? identityNumber)
        {
            var hasIdentityType =
                identityType.HasValue;

            var hasIdentityNumber =
                !string.IsNullOrWhiteSpace(
                    identityNumber
                );

            if (hasIdentityType != hasIdentityNumber)
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .InvalidTenantIdentityInformation
                );
            }
        }

        private static string? NormalizeIdentityNumber(
            string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value
                .Trim()
                .ToUpperInvariant();
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
