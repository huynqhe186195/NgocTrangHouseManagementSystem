using System;
using System.Linq;
using System.Reflection;
using BuildingManagement.Buildings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Authorization;
using Xunit;

namespace NgocTrangHouseManagementSystem.Security.Tests;

/// <summary>
/// MVC model-only checks; do not start the host or touch an application DB.
/// A later HTTP-host test must verify actual endpoint discovery and 401s.
/// </summary>
public sealed class BuildingManagementAuthenticatedControllerConvention_Tests
{
    [Fact]
    public void AUTHMODEL01_Building_AppService_Gets_Authenticated_User_Policy()
    {
        var controller = ModelFor(typeof(BuildingAppService));
        var application = AppWith(controller);

        new BuildingManagementAuthenticatedControllerConvention().Apply(application);

        var auth = Assert.Single(controller.Filters.OfType<AuthorizeFilter>());
        Assert.NotNull(auth.Policy);
        Assert.Contains(auth.Policy.Requirements,
            requirement => requirement is DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public void AUTHMODEL02_Unrelated_Controllers_Remain_Unchanged()
    {
        var controller = ModelFor(typeof(UnrelatedTestController));
        new BuildingManagementAuthenticatedControllerConvention()
            .Apply(AppWith(controller));

        Assert.Empty(controller.Filters.OfType<AuthorizeFilter>());
    }

    [Fact]
    public void AUTHMODEL03_Class_Anonymous_Opt_Out_Is_Refused()
    {
        var controller = new ControllerModel(
            typeof(BuildingAppService).GetTypeInfo(),
            new object[] { new AllowAnonymousAttribute() });

        Assert.Throws<InvalidOperationException>(() =>
            new BuildingManagementAuthenticatedControllerConvention()
                .Apply(AppWith(controller)));
    }

    [Fact]
    public void AUTHMODEL04_Action_Anonymous_Opt_Out_Is_Refused()
    {
        var controller = ModelFor(typeof(BuildingAppService));
        var method = typeof(BuildingAppService)
            .GetMethod(nameof(BuildingAppService.GetAsync))!;
        controller.Actions.Add(new ActionModel(
            method, new object[] { new AllowAnonymousAttribute() }));

        Assert.Throws<InvalidOperationException>(() =>
            new BuildingManagementAuthenticatedControllerConvention()
                .Apply(AppWith(controller)));
    }

    [Fact]
    public void AUTHMODEL05_Invoice_Draft_Remote_Disable_Is_Not_Changed()
    {
        var serviceType =
            typeof(BuildingManagement.Invoices.InvoiceDraftStorageAppService);
        var attribute = serviceType.GetCustomAttribute<Volo.Abp.RemoteServiceAttribute>();

        Assert.NotNull(attribute);
        Assert.False(attribute.IsEnabled);
    }

    private static ControllerModel ModelFor(Type serviceType)
        => new(serviceType.GetTypeInfo(), Array.Empty<object>());

    private static ApplicationModel AppWith(ControllerModel controller)
    {
        var application = new ApplicationModel();
        application.Controllers.Add(controller);
        return application;
    }

    private sealed class UnrelatedTestController { }
}
