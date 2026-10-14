using System;
using System.ComponentModel.DataAnnotations;

namespace BuildingManagement.Tenants
{
    public class CreateTenantDto
    {
        [Required]
        [MaxLength(128)]
        public string FullName { get; set; } = default!;

        [Required]
        [MaxLength(20)]
        public string PhoneNumber { get; set; } = default!;

        [EmailAddress]
        [MaxLength(256)]
        public string? Email { get; set; }

        public DateTime? DateOfBirth { get; set; }

        public Gender? Gender { get; set; }

        public IdentityType IdentityType { get; set; } = IdentityType.CitizenId;

        [Required]
        [MaxLength(50)]
        public string IdentityNumber { get; set; } = default!;

        [MaxLength(256)]
        public string? PermanentAddress { get; set; }

        [MaxLength(128)]
        public string? EmergencyContactName { get; set; }

        [MaxLength(20)]
        public string? EmergencyContactPhone { get; set; }

        [MaxLength(500)]
        public string? Notes { get; set; }
    }
}