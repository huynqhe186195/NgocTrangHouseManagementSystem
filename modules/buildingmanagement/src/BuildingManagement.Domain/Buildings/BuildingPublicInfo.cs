using System;
using System.Collections.Generic;
using System.Text.Json;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.Buildings;

/// <summary>
/// What the owner publishes about a building to prospective tenants: the
/// prices quoted on the room screen and the house rules beside them. This is
/// display copy, deliberately separate from UtilityRate, which the invoice
/// pipeline reads as billing evidence. Keeping them apart means an edit here
/// can never silently change what a tenant is charged.
/// </summary>
public class BuildingPublicInfo : FullAuditedAggregateRoot<Guid>
{
    public Guid BuildingId { get; private set; }

    public decimal? ElectricityUnitPrice { get; private set; }

    public decimal? WaterUnitPrice { get; private set; }

    public decimal? ServiceFee { get; private set; }

    public string? OtherInfo { get; private set; }

    /// <summary>
    /// JSON array of {icon, text}, in published order. Stored as a string
    /// rather than a child table for the same reason Room.LayoutPoints is:
    /// the list is display copy that is always read whole, never filtered.
    /// </summary>
    public string? HouseRulesJson { get; private set; }

    protected BuildingPublicInfo()
    {
    }

    public BuildingPublicInfo(
        Guid id,
        Guid buildingId,
        decimal? electricityUnitPrice = null,
        decimal? waterUnitPrice = null,
        decimal? serviceFee = null,
        string? otherInfo = null) : base(id)
    {
        BuildingId = buildingId;
        Update(electricityUnitPrice, waterUnitPrice, serviceFee, otherInfo);
    }

    public void Update(
        decimal? electricityUnitPrice,
        decimal? waterUnitPrice,
        decimal? serviceFee,
        string? otherInfo)
    {
        ElectricityUnitPrice = electricityUnitPrice;
        WaterUnitPrice = waterUnitPrice;
        ServiceFee = serviceFee;
        OtherInfo = otherInfo;
    }

    public void ReplaceHouseRules(IEnumerable<(HouseRuleIcon Icon, string Text)> rules)
    {
        var serialized = new List<HouseRuleEntry>();
        foreach (var (icon, text) in rules)
        {
            serialized.Add(new HouseRuleEntry
            {
                Icon = icon,
                Text = text.Trim()
            });
        }

        HouseRulesJson = serialized.Count == 0
            ? null
            : JsonSerializer.Serialize(serialized);
    }
}

internal sealed class HouseRuleEntry
{
    public HouseRuleIcon Icon { get; set; }

    public string Text { get; set; } = default!;
}
