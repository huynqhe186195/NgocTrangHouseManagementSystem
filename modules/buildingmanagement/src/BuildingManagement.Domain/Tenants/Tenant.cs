using System;
using System.Collections.Generic;
using System.Text;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.Tenants
{
    public class Tenant : FullAuditedAggregateRoot<Guid>
    {
        public string FullName { get; private set; } = default!;

        public string? PhoneNumber { get; private set; }

        public string? Email { get; private set; }

        public DateTime? DateOfBirth { get; private set; }

        public Gender? Gender { get; private set; }

        public IdentityType? IdentityType { get; private set; }

        public string? IdentityNumber { get; private set; }

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
            string? phoneNumber = null,
            string? email = null,
            DateTime? dateOfBirth = null,
            Gender? gender = null,
            IdentityType? identityType = null,
            string? identityNumber = null,
            string? permanentAddress = null,
            string? emergencyContactName = null,
            string? emergencyContactPhone = null,
            string? notes = null
        ) : base(id)
        {
            FullName = fullName;
            PhoneNumber = phoneNumber;
            Email = email;
            DateOfBirth = dateOfBirth;
            Gender = gender;
            IdentityType = identityType;
            IdentityNumber = identityNumber;
            PermanentAddress = permanentAddress;
            EmergencyContactName = emergencyContactName;
            EmergencyContactPhone = emergencyContactPhone;
            Notes = notes;
        }

        public void Update(
            string fullName,
            string? phoneNumber,
            string? email,
            DateTime? dateOfBirth,
            Gender? gender,
            IdentityType? identityType,
            string? identityNumber,
            string? permanentAddress,
            string? emergencyContactName,
            string? emergencyContactPhone,
            string? notes
        )
        {
            FullName = fullName;
            PhoneNumber = phoneNumber;
            Email = email;
            DateOfBirth = dateOfBirth;
            Gender = gender;
            IdentityType = identityType;
            IdentityNumber = identityNumber;
            PermanentAddress = permanentAddress;
            EmergencyContactName = emergencyContactName;
            EmergencyContactPhone = emergencyContactPhone;
            Notes = notes;
        }
    }
}
