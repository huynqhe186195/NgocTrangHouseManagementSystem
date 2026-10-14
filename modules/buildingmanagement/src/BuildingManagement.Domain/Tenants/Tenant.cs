using System;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.Tenants
{
    public class Tenant
        : FullAuditedAggregateRoot<Guid>
    {
        public string FullName { get; private set; }
            = default!;

        public string PhoneNumber { get; private set; }
            = default!;

        public string? Email { get; private set; }

        public DateTime? DateOfBirth { get; private set; }

        public Gender? Gender { get; private set; }

        public IdentityType IdentityType { get; private set; }

        public string IdentityNumber { get; private set; }
            = default!;

        public string? PermanentAddress { get; private set; }

        public string? EmergencyContactName { get; private set; }

        public string? EmergencyContactPhone { get; private set; }

        public string? Notes { get; private set; }

        protected Tenant()
        {
        }

        public Tenant(
            Guid id,
            string fullName,
            string phoneNumber,
            string? email,
            DateTime? dateOfBirth,
            Gender? gender,
            IdentityType identityType,
            string identityNumber,
            string? permanentAddress = null,
            string? emergencyContactName = null,
            string? emergencyContactPhone = null,
            string? notes = null
        ) : base(id)
        {
            SetRequiredInformation(
                fullName,
                phoneNumber,
                identityType,
                identityNumber
            );

            Email =
                email;

            DateOfBirth =
                dateOfBirth;

            Gender =
                gender;

            PermanentAddress =
                permanentAddress;

            EmergencyContactName =
                emergencyContactName;

            EmergencyContactPhone =
                emergencyContactPhone;

            Notes =
                notes;
        }

        public void Update(
            string fullName,
            string phoneNumber,
            string? email,
            DateTime? dateOfBirth,
            Gender? gender,
            IdentityType identityType,
            string identityNumber,
            string? permanentAddress,
            string? emergencyContactName,
            string? emergencyContactPhone,
            string? notes
        )
        {
            SetRequiredInformation(
                fullName,
                phoneNumber,
                identityType,
                identityNumber
            );

            Email =
                email;

            DateOfBirth =
                dateOfBirth;

            Gender =
                gender;

            PermanentAddress =
                permanentAddress;

            EmergencyContactName =
                emergencyContactName;

            EmergencyContactPhone =
                emergencyContactPhone;

            Notes =
                notes;
        }

        private void SetRequiredInformation(
            string fullName,
            string phoneNumber,
            IdentityType identityType,
            string identityNumber)
        {
            FullName =
                Check.NotNullOrWhiteSpace(
                    fullName,
                    nameof(fullName),
                    128
                );

            PhoneNumber =
                Check.NotNullOrWhiteSpace(
                    phoneNumber,
                    nameof(phoneNumber),
                    20
                );

            if (
                identityType !=
                    IdentityType.CitizenId
            )
            {
                throw new BusinessException(
                    BuildingManagementErrorCodes
                        .TenantIdentityMustBeCitizenId
                );
            }

            IdentityType =
                identityType;

            IdentityNumber =
                Check.NotNullOrWhiteSpace(
                    identityNumber,
                    nameof(identityNumber),
                    50
                );
        }
    }
}