using System;
using System.Collections.Generic;
using System.Reflection;
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
