using System;
using System.Collections.Generic;
using BuildingManagement.RentalAccounts;

namespace BuildingManagement.Mobile;

public sealed class TenantMobileOverviewDto
{
    public TenantMobileUserDto CurrentUser { get; set; } = default!;
    public TenantMobileAccountDto Account { get; set; } = default!;
    public TenantMobileContractDto? ActiveContract { get; set; }
    public List<TenantMobileMemberDto> Members { get; set; } = new();
}

public sealed class TenantMobileUserDto
{
    public Guid IdentityUserId { get; set; }
    public Guid? TenantId { get; set; }
    public string? DisplayName { get; set; }
    public RentalAccountAccessRole AccessRole { get; set; }
}

public sealed class TenantMobileAccountDto
{
    public Guid Id { get; set; }
    public string AccountNumber { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public bool IsActive { get; set; }
}

public sealed class TenantMobileContractDto
{
    public Guid Id { get; set; }
    public string ContractNumber { get; set; } = default!;
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public TenantMobileRoomDto Room { get; set; } = default!;
}

public sealed class TenantMobileRoomDto
{
    public Guid Id { get; set; }
    public string RoomNumber { get; set; } = default!;
    public string Name { get; set; } = default!;
    public Guid FloorId { get; set; }
    public string FloorName { get; set; } = default!;
    public Guid BuildingId { get; set; }
    public string BuildingName { get; set; } = default!;
}

public sealed class TenantMobileMemberDto
{
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = default!;
    public string Role { get; set; } = default!;
}
