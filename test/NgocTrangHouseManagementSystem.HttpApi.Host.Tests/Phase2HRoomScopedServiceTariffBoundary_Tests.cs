using System;
using System.Linq;
using System.Reflection;
using BuildingManagement.RoomChanges;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp.Uow;
using Xunit;

namespace NgocTrangHouseManagementSystem.Security.Tests;

/// <summary>
/// Route and contract guard regressions. These reflect real controller and
/// DTO types but are NOT substitutes for authenticated cross-building HTTP
/// requests against the disposable two-PostgreSQL staging environment.
/// </summary>
public sealed class Phase2HRoomScopedServiceTariffBoundary_Tests
{
    [Fact]
    public void TRFSVCHTTP01_Only_Authenticated_Staging_Read_And_Append_Exposed()
    {
        var type = typeof(SliceBStagingRoomScopedServiceTariffsController);
        Assert.NotNull(type.GetCustomAttribute<ApiControllerAttribute>());
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        Assert.Equal("api/slice-b-staging/rooms/{roomId:guid}/service-tariffs",
            type.GetCustomAttribute<RouteAttribute>()?.Template);

        var actions = type.GetMethods(BindingFlags.Public |
            BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.Equal(2, actions.Length);
        var read = Assert.Single(actions.Where(x => x.Name == "GetListAsync"));
        Assert.NotNull(read.GetCustomAttribute<HttpGetAttribute>());
        Assert.NotNull(read.GetCustomAttribute<UnitOfWorkAttribute>());
        var append = Assert.Single(actions.Where(x => x.Name == "AppendAsync"));
        Assert.NotNull(append.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(append.GetCustomAttribute<UnitOfWorkAttribute>());
        Assert.DoesNotContain(actions, x => x.GetCustomAttribute<HttpPutAttribute>() is not null ||
            x.GetCustomAttribute<HttpDeleteAttribute>() is not null);
    }

    [Fact]
    public void TRFSVCHTTP02_Actor_And_Tariff_Version_Are_Server_Owned()
    {
        var dto = typeof(SliceBRoomScopedServiceTariffInput)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(x => x.Name).ToArray();
        Assert.Contains("ServiceId", dto);
        Assert.Contains("UnitPriceVnd", dto);
        Assert.Contains("EffectiveFrom", dto);
        Assert.DoesNotContain("Version", dto);
        Assert.DoesNotContain("ApprovedByUserId", dto);
        Assert.DoesNotContain("ApprovedAtUtc", dto);
        Assert.DoesNotContain("EvidenceSha256", dto);

        var actions = typeof(SliceBStagingRoomScopedServiceTariffs)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance |
                BindingFlags.DeclaredOnly).Select(x => x.Name).ToArray();
        Assert.Equal(2, actions.Length);
        Assert.Contains("GetListAsync", actions);
        Assert.Contains("AppendAsync", actions);
        Assert.DoesNotContain(actions, x =>
            x.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("Delete", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("Refund", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("PostInvoice", StringComparison.OrdinalIgnoreCase));

        var preview = new RoomScopedServiceChargePreview(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30),
            2, null, RoomScopedServiceBillingUnit.PerPerson,
            RoomScopedServiceChargingRule.WholeMonth, 150_000m, 300_000m,
            new string('a', 64));
        Assert.False(preview.CanPost);
        Assert.False(preview.IsIssuedInvoice);
        Assert.False(preview.IsBankReceipt);
    }
}
