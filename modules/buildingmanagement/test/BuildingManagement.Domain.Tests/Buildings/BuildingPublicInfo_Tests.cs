using System;
using System.Text.Json;
using BuildingManagement.Buildings;
using Xunit;

namespace BuildingManagement.DomainTests.Buildings;

public sealed class BuildingPublicInfo_Tests
{
    private static BuildingPublicInfo Info() => new(
        Guid.NewGuid(), Guid.NewGuid(),
        3_500m, 100_000m, 150_000m, "Gửi xe máy miễn phí");

    [Fact]
    public void PUB_01_Prices_Are_Kept_As_Published()
    {
        var info = Info();
        Assert.Equal(3_500m, info.ElectricityUnitPrice);
        Assert.Equal(100_000m, info.WaterUnitPrice);
        Assert.Equal(150_000m, info.ServiceFee);
        Assert.Equal("Gửi xe máy miễn phí", info.OtherInfo);
    }

    [Fact]
    public void PUB_02_No_Rules_Stores_Null_Not_An_Empty_Array()
    {
        var info = Info();
        info.ReplaceHouseRules(Array.Empty<(HouseRuleIcon, string)>());
        Assert.Null(info.HouseRulesJson);
    }

    [Fact]
    public void PUB_03_Rules_Keep_Their_Order_And_Trim_Text()
    {
        var info = Info();
        info.ReplaceHouseRules(new[]
        {
            (HouseRuleIcon.Prohibition, "  Không nuôi thú cưng  "),
            (HouseRuleIcon.QuietHours, "Giờ yên tĩnh 22:00-06:00")
        });

        using var doc = JsonDocument.Parse(info.HouseRulesJson!);
        var rules = doc.RootElement;
        Assert.Equal(2, rules.GetArrayLength());
        Assert.Equal((int)HouseRuleIcon.Prohibition,
            rules[0].GetProperty("Icon").GetInt32());
        Assert.Equal("Không nuôi thú cưng",
            rules[0].GetProperty("Text").GetString());
        Assert.Equal((int)HouseRuleIcon.QuietHours,
            rules[1].GetProperty("Icon").GetInt32());
    }

    [Fact]
    public void PUB_04_Replacing_Rules_Drops_The_Previous_List()
    {
        var info = Info();
        info.ReplaceHouseRules(new[]
        {
            (HouseRuleIcon.Prohibition, "A"),
            (HouseRuleIcon.Prohibition, "B")
        });
        info.ReplaceHouseRules(new[] { (HouseRuleIcon.General, "C") });

        using var doc = JsonDocument.Parse(info.HouseRulesJson!);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("C", doc.RootElement[0].GetProperty("Text").GetString());
    }

    [Fact]
    public void PUB_05_Unpublishing_A_Price_Clears_It()
    {
        var info = Info();
        info.Update(null, null, null, null);
        Assert.Null(info.ElectricityUnitPrice);
        Assert.Null(info.WaterUnitPrice);
        Assert.Null(info.ServiceFee);
        Assert.Null(info.OtherInfo);
    }
}
