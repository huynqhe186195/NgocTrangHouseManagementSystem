using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace BuildingManagement.Buildings;

public class BuildingPublicInfoDto
{
    public Guid BuildingId { get; set; }

    public decimal? ElectricityUnitPrice { get; set; }

    public decimal? WaterUnitPrice { get; set; }

    public decimal? ServiceFee { get; set; }

    public string? OtherInfo { get; set; }

    public List<HouseRuleItemDto> HouseRules { get; set; } = new();
}

public class HouseRuleItemDto
{
    public HouseRuleIcon Icon { get; set; }

    [Required]
    [MaxLength(200)]
    public string Text { get; set; } = default!;
}

/// <summary>
/// Every field is optional: the owner may publish only the electricity price,
/// or only the rules. A missing price renders as "chưa công bố" rather than
/// as a zero the tenant might read as free.
/// </summary>
public class UpdateBuildingPublicInfoDto
{
    [Range(0, 1_000_000_000)]
    public decimal? ElectricityUnitPrice { get; set; }

    [Range(0, 1_000_000_000)]
    public decimal? WaterUnitPrice { get; set; }

    [Range(0, 1_000_000_000)]
    public decimal? ServiceFee { get; set; }

    [MaxLength(1000)]
    public string? OtherInfo { get; set; }

    [MaxLength(20)]
    public List<HouseRuleItemDto> HouseRules { get; set; } = new();
}
