using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using NgocTrangHouseManagementSystem.Payments;
using Xunit;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

public sealed class PayOsStagingD04BController_Tests
{
    [Theory]
    [InlineData("Production", true)]
    [InlineData("Development", true)]
    [InlineData("Staging", false)]
    public async Task D04B_routes_are_disabled_by_default(
        string environment, bool enabled)
    {
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(x => x.EnvironmentName).Returns(environment);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["BuildingManagement:PayOsD04B:Enabled"] =
                    enabled ? "true" : "false",
                ["BuildingManagement:PayOsD04B:Activation"] =
                    "I_ACCEPT_DISPOSABLE_D04B_DUAL_AUTH_ONLY"
            }).Build();
        var controller = new PayOsD04StagingReviewController(host.Object, config)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        Assert.IsType<NotFoundResult>(await controller.ReviewAsync(
            Guid.NewGuid(), new PayOsD04ReviewForm()));
        Assert.IsType<NotFoundResult>(await controller.ApproveAsync(Guid.NewGuid()));
        Assert.IsType<NotFoundResult>(await controller.PostAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Valid_staging_flag_cannot_enable_D04B_without_secure_DI()
    {
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(x => x.EnvironmentName).Returns("Staging");
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["BuildingManagement:PayOsD04B:Enabled"] = "true",
                ["BuildingManagement:PayOsD04B:Activation"] =
                    "I_ACCEPT_DISPOSABLE_D04B_DUAL_AUTH_ONLY"
            }).Build();
        var controller = new PayOsD04StagingReviewController(host.Object, config)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        Assert.IsType<NotFoundResult>(await controller.ApproveAsync(Guid.NewGuid()));
        Assert.IsType<NotFoundResult>(await controller.PostAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Even_with_injected_service_D04B_must_refuse_wrong_database_or_merchant_mode()
    {
        // This tests only the controller activation boundary. It does NOT
        // simulate ASP.NET authorization middleware or logged-in ABP users.
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(x => x.EnvironmentName).Returns("Staging");
        const string loopback =
            "Host=127.0.0.1;Port=15432;Username=postgres;Password=disposable-only;Database=";
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["BuildingManagement:PayOsD04B:Enabled"] = "true",
                ["BuildingManagement:PayOsD04B:Activation"] =
                    "I_ACCEPT_DISPOSABLE_D04B_DUAL_AUTH_ONLY",
                ["BuildingManagement:PayOsStaging:Enabled"] = "false",
                ["ConnectionStrings:Default"] =
                    loopback + "payos_d04_identity_test",
                ["ConnectionStrings:BuildingManagement"] =
                    loopback + "payos_d04_legacy_test",
                ["ConnectionStrings:PayOsD04BStaging"] =
                    loopback + "payos_ef_migration_test"
            }).Build();
        var reviewer = new Mock<IPayOsD04ManualEvidenceService>();
        reviewer.Setup(x => x.ApproveReviewedDepositAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        var controller = new PayOsD04StagingReviewController(
            host.Object, config, reviewer.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        var id = Guid.NewGuid();

        // Correct triple-DB location allows this controller to reach the
        // injected service (the real service still enforces live SQL rights).
        Assert.IsType<AcceptedResult>(await controller.ApproveAsync(id));
        reviewer.Verify(x => x.ApproveReviewedDepositAsync(
            id, It.IsAny<CancellationToken>()), Times.Once);

        // Replace the Building DB with an actual user database name; even
        // valid staging activation plus a registered service must fail closed.
        config["ConnectionStrings:BuildingManagement"] =
            loopback + "NgocTrangDb";
        Assert.IsType<NotFoundResult>(await controller.ApproveAsync(id));
        reviewer.Verify(x => x.ApproveReviewedDepositAsync(
            id, It.IsAny<CancellationToken>()), Times.Once);

        config["ConnectionStrings:BuildingManagement"] =
            loopback + "payos_d04_legacy_test";
        config["BuildingManagement:PayOsStaging:Enabled"] = "true";
        Assert.IsType<NotFoundResult>(await controller.ApproveAsync(id));
        reviewer.Verify(x => x.ApproveReviewedDepositAsync(
            id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Every_D04B_route_requires_authentication_and_hidden_exploration()
    {
        var type = typeof(PayOsD04StagingReviewController);
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(type.GetCustomAttribute<ApiExplorerSettingsAttribute>());
        Assert.True(type.GetCustomAttribute<ApiExplorerSettingsAttribute>()!.IgnoreApi);
        foreach (var method in new[]
        {
            nameof(PayOsD04StagingReviewController.ReviewAsync),
            nameof(PayOsD04StagingReviewController.ApproveAsync),
            nameof(PayOsD04StagingReviewController.PostAsync)
        })
        {
            Assert.Null(type.GetMethod(method)!
                .GetCustomAttribute<AllowAnonymousAttribute>());
        }
    }
}
