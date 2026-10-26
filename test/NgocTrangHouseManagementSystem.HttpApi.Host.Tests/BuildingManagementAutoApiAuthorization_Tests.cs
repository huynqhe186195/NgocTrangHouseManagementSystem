using System;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using System.Text.RegularExpressions;
using System.Text;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using BuildingManagement;
using BuildingManagement.Buildings;
using BuildingManagement.Invoices;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.AspNetCore.Mvc.Libs;
using Volo.Abp.Autofac;
using Volo.Abp.Modularity;
using Xunit;

namespace NgocTrangHouseManagementSystem.Security.Tests;

/// <summary>
/// Starts a minimal, IN-MEMORY ABP application that uses the real ABP
/// conventional-controller discovery and the actual production MVC convention.
/// It deliberately excludes the EF modules, DB migrators and application host.
/// These tests do NOT establish production authorization or building scope.
/// </summary>
public sealed class BuildingManagementAutoApiAuthorization_Tests
{
    [Fact]
    public async Task APIAUTO01_Abp_Generated_Business_Actions_Are_Authenticated()
    {
        await using var app = await StartIsolatedAbpHostAsync();

        var actions = GetGeneratedActions(app);
        Assert.NotEmpty(actions);

        // The actual BuildingAppService must be discovered by ABP as an
        // HTTP controller: validating only an explicitly authored controller
        // would leave the conventional service attack surface unverified.
        Assert.Contains(actions, action =>
            action.ControllerTypeInfo.AsType() == typeof(BuildingAppService) &&
            action.MethodInfo.Name == nameof(BuildingAppService.GetListAsync));
        Assert.Contains(actions, action =>
            action.ControllerTypeInfo.AsType() == typeof(BuildingAppService) &&
            action.MethodInfo.Name == nameof(BuildingAppService.CreateAsync));

        foreach (var action in actions)
        {
            Assert.DoesNotContain(action.EndpointMetadata,
                metadata => metadata is Microsoft.AspNetCore.Authorization.IAllowAnonymous);

            Assert.Contains(action.FilterDescriptors, descriptor =>
                descriptor.Filter is AuthorizeFilter filter &&
                filter.Policy is not null &&
                filter.Policy.Requirements.Any(requirement =>
                    requirement is DenyAnonymousAuthorizationRequirement));
        }

        // Remote-disabled draft endpoints must never be generated.
        Assert.DoesNotContain(actions, action =>
            action.ControllerTypeInfo.AsType() == typeof(InvoiceDraftStorageAppService));
    }

    [Fact]
    public async Task APIAUTO02_Anonymous_Generated_Building_GetList_Returns_401()
    {
        await using var app = await StartIsolatedAbpHostAsync();

        var action = Assert.Single(GetGeneratedActions(app), x =>
            x.ControllerTypeInfo.AsType() == typeof(BuildingAppService) &&
            x.MethodInfo.Name == nameof(BuildingAppService.GetListAsync));

        var route = Assert.IsType<string>(action.AttributeRouteInfo?.Template);
        Assert.False(string.IsNullOrWhiteSpace(route));
        Assert.Contains("building-management", route,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{", route);

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/" + route.TrimStart('/'));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task APIAUTO03_Actual_Route_Endpoints_Require_Authentication()
    {
        await using var app = await StartIsolatedAbpHostAsync();

        // EndpointDataSource describes the mapped HTTP surface, rather than
        // assuming that an MVC action descriptor was actually routed.
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Select(endpoint => new
            {
                Endpoint = endpoint,
                Action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()
            })
            .Where(x => x.Action is not null &&
                        (x.Action.ControllerTypeInfo.Assembly ==
                             typeof(BuildingManagementApplicationModule).Assembly ||
                         x.Action.ControllerTypeInfo.Assembly ==
                             typeof(BuildingManagementHttpApiModule).Assembly))
            .ToArray();

        Assert.NotEmpty(endpoints);
        Assert.Contains(endpoints, x =>
            x.Action!.ControllerTypeInfo.AsType() == typeof(BuildingAppService) &&
            x.Action.MethodInfo.Name == nameof(BuildingAppService.GetListAsync));
        Assert.Contains(endpoints, x =>
            x.Action!.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Samples.ExampleController));

        foreach (var item in endpoints)
        {
            Assert.DoesNotContain(item.Endpoint.Metadata,
                metadata => metadata is IAllowAnonymous);
            Assert.Contains(item.Action!.FilterDescriptors, descriptor =>
                descriptor.Filter is AuthorizeFilter filter &&
                filter.Policy is not null &&
                filter.Policy.Requirements.Any(requirement =>
                    requirement is DenyAnonymousAuthorizationRequirement));
        }
    }

    [Fact]
    public async Task APIAUTO04_Anonymous_Generated_Create_Cannot_Reach_Business_Service()
    {
        await using var app = await StartIsolatedAbpHostAsync();

        var create = Assert.Single(GetGeneratedActions(app), x =>
            x.ControllerTypeInfo.AsType() == typeof(BuildingAppService) &&
            x.MethodInfo.Name == nameof(BuildingAppService.CreateAsync));

        var route = Assert.IsType<string>(create.AttributeRouteInfo?.Template);
        using var client = app.GetTestClient();
        using var payload = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(
            "/" + route.TrimStart('/'), payload);

        // Even invalid input must not be deserialized/processed by a business
        // action before the anonymous caller is challenged.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task APIAUTO05_Anonymous_Known_Id_Reads_Are_Challenged_Across_Services()
    {
        await using var app = await StartIsolatedAbpHostAsync();

        var resources = new[] { "RoomAppService", "ContractAppService",
                                "PaymentAppService" };
        using var client = app.GetTestClient();

        foreach (var service in resources)
        {
            var action = Assert.Single(GetGeneratedActions(app), x =>
                x.ControllerTypeInfo.Name == service &&
                x.MethodInfo.Name == "GetAsync");
            var route = Assert.IsType<string>(action.AttributeRouteInfo?.Template);
            var path = Regex.Replace(route, @"\\{[^}]+\\}",
                Guid.NewGuid().ToString("D"));

            using var response = await client.GetAsync("/" + path.TrimStart('/'));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    private static ControllerActionDescriptor[] GetGeneratedActions(WebApplication app)
        => app.Services
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(action => action.ControllerTypeInfo.Assembly ==
                typeof(BuildingManagementApplicationModule).Assembly)
            .ToArray();

    private static async Task<WebApplication> StartIsolatedAbpHostAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseTestServer();
        builder.Host.UseAutofac();

        await builder.AddApplicationAsync<BuildingManagementApiInventoryTestModule>();
        var app = builder.Build();
        await app.InitializeApplicationAsync();
        await app.StartAsync();
        return app;
    }

    [DependsOn(
        typeof(AbpAutofacModule),
        typeof(BuildingManagementApplicationModule),
        typeof(BuildingManagementHttpApiModule))]
    public sealed class BuildingManagementApiInventoryTestModule : AbpModule
    {
        public override void PreConfigureServices(ServiceConfigurationContext context)
        {
            PreConfigure<AbpAspNetCoreMvcOptions>(options =>
            {
                options.ConventionalControllers.Create(
                    typeof(BuildingManagementApplicationModule).Assembly,
                    settings => settings.RootPath = "building-management");
            });
        }

        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            context.Services.AddAuthentication("ApiAutoProbe")
                .AddScheme<AuthenticationSchemeOptions,
                    RejectAnonymousAuthenticationHandler>(
                    "ApiAutoProbe", _ => { });
            context.Services.AddAuthorization();

            Configure<AbpMvcLibsOptions>(options => options.CheckLibs = false);
            Configure<MvcOptions>(options =>
                options.Conventions.Add(
                    new BuildingManagementAuthenticatedControllerConvention()));
        }

        public override void OnApplicationInitialization(
            ApplicationInitializationContext context)
        {
            var app = context.GetApplicationBuilder();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseConfiguredEndpoints();
        }
    }

    private sealed class RejectAnonymousAuthenticationHandler
        : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public RejectAnonymousAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.NoResult());
    }
}
