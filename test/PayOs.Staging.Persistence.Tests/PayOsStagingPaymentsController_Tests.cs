using System;
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

public class PayOsStagingPaymentsController_Tests
{
    private static PayOsStagingPaymentsController DisabledController(
        string environment = "Production", bool enabled = true)
    {
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(x => x.EnvironmentName).Returns(environment);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new System.Collections.Generic.Dictionary<string, string?>
            {
                ["BuildingManagement:PayOsStaging:Enabled"] = enabled ? "true" : "false",
                ["BuildingManagement:PayOsStaging:Activation"] =
                    "I_ACCEPT_ISOLATED_PAYOS_STAGING_ONLY"
            }).Build();
        return new PayOsStagingPaymentsController(host.Object, config)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Development", true)]
    [InlineData("Staging", false)]
    public async Task Non_staging_or_disabled_controller_cannot_issue_checkouts(
        string environment, bool enabled)
    {
        var controller = DisabledController(environment, enabled);
        Assert.IsType<NotFoundResult>(await controller.CheckoutAsync(Guid.NewGuid()));
        Assert.IsType<NotFoundResult>(await controller.ReconcileAsync(Guid.NewGuid()));
        Assert.IsType<NotFoundResult>(await controller.WebhookAsync());
    }

    [Fact]
    public void Webhook_alone_is_anonymous_but_checkout_and_review_require_authentication()
    {
        var type = typeof(PayOsStagingPaymentsController);
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(type.GetMethod(nameof(PayOsStagingPaymentsController.WebhookAsync))!
            .GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Null(type.GetMethod(nameof(PayOsStagingPaymentsController.CheckoutAsync))!
            .GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Null(type.GetMethod(nameof(PayOsStagingPaymentsController.ReconcileAsync))!
            .GetCustomAttribute<AllowAnonymousAttribute>());
    }
}
