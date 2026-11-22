using System;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.RentalAccounts;

public class RentalAccountUser : FullAuditedEntity<Guid>
{
    public Guid RentalAccountId { get; private set; }

    // Identity lives in the separate Identity database; this is an application-level link.
    public Guid IdentityUserId { get; private set; }

    public Guid? TenantId { get; private set; }

    public RentalAccountAccessRole AccessRole { get; private set; }

    public bool IsActive { get; private set; }

    public DateTime? RevokedAt { get; private set; }

    protected RentalAccountUser()
    {
    }

    public RentalAccountUser(
        Guid id,
        Guid rentalAccountId,
        Guid identityUserId,
        Guid? tenantId,
        RentalAccountAccessRole accessRole)
        : base(id)
    {
        RentalAccountId = rentalAccountId;
        IdentityUserId = identityUserId;
        TenantId = tenantId;
        AccessRole = accessRole;
        IsActive = true;
    }

    public void Update(
        Guid? tenantId,
        RentalAccountAccessRole accessRole)
    {
        TenantId = tenantId;
        AccessRole = accessRole;
    }

    public void Revoke(DateTime revokedAt)
    {
        IsActive = false;
        RevokedAt = revokedAt;
    }

    public void Reactivate()
    {
        IsActive = true;
        RevokedAt = null;
    }
}
