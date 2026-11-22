using System;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.RentalAccounts;

public class RentalAccount : FullAuditedAggregateRoot<Guid>
{
    public string AccountNumber { get; private set; } = default!;

    public string DisplayName { get; private set; } = default!;

    public bool IsActive { get; private set; }

    protected RentalAccount()
    {
    }

    public RentalAccount(
        Guid id,
        string accountNumber,
        string displayName)
        : base(id)
    {
        SetIdentity(accountNumber, displayName);
        IsActive = true;
    }

    public void Update(string accountNumber, string displayName)
    {
        SetIdentity(accountNumber, displayName);
    }

    public void Deactivate()
    {
        IsActive = false;
    }

    public void Reactivate()
    {
        IsActive = true;
    }

    private void SetIdentity(string accountNumber, string displayName)
    {
        AccountNumber = Check.NotNullOrWhiteSpace(accountNumber, nameof(accountNumber), 64);
        DisplayName = Check.NotNullOrWhiteSpace(displayName, nameof(displayName), 128);
    }
}
