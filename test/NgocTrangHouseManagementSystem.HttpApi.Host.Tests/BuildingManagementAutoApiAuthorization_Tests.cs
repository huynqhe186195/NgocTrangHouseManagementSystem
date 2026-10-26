using System;
using System.Linq;
using System.Net;
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

        var route = action.AttributeRouteInfo?.Template;
        Assert.False(string.IsNullOrWhiteSpace(route));
        Assert.Contains("building-management", route,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{", route);

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/" + route.TrimStart('/'));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
