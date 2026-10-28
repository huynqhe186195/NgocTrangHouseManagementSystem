using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using Volo.Abp.Domain.Repositories;
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
            var path = Regex.Replace(route, @"\{[^}]+\}",
                Guid.NewGuid().ToString("D"));

            using var response = await client.GetAsync("/" + path.TrimStart('/'));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task APIAUTO06_Identity_Adapter_Does_Not_Replace_Default_Deny()
    {
        await using var app = await StartIsolatedAbpHostAsync();

        // The candidate ABP Identity account adapter must not become a
        // production-activated grant source merely because it is compiled.
        var activeAccounts = app.Services
            .GetRequiredService<BuildingManagement.Security.IBuildingOperatorActiveAccountReader>();
        Assert.IsType<BuildingManagement.Security.DenyAllBuildingOperatorActiveAccountReader>(
            activeAccounts);
        Assert.False(await activeAccounts.IsActiveAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task APIAUTO07_Scoped_Read_Gate_Is_Not_A_Remote_Controller()
    {
        await using var app = await StartIsolatedAbpHostAsync();

        var generated = GetGeneratedActions(app);
        Assert.DoesNotContain(generated, action =>
            action.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Security.BuildingScopedReadDesignGate));
        Assert.DoesNotContain(generated, action =>
            action.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Security.BuildingOperatorChangeDesignGate));

        var grants = app.Services.GetRequiredService<
            BuildingManagement.Security.IBuildingOperatorGrantReader>();
        Assert.IsType<
            BuildingManagement.Security.DenyAllBuildingOperatorGrantReader>(grants);
        Assert.Empty(await grants.GetActiveBuildingIdsAsync(Guid.NewGuid()));
        var writer = app.Services.GetRequiredService<
            BuildingManagement.Security.IBuildingOperatorGrantMutationWriter>();
        Assert.IsType<
            BuildingManagement.Security.DenyAllBuildingOperatorGrantMutationWriter>(writer);
        await Assert.ThrowsAsync<Volo.Abp.Authorization.AbpAuthorizationException>(() =>
            writer.AssignAtomicallyAsync(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null));
        // No producer may accidentally register the staged PostgreSQL
        // boundary as a permissive public write executor.
        var writeBoundary = app.Services.GetRequiredService<
            BuildingManagement.Security.IBuildingScopedWriteBoundary>();
        Assert.IsType<
            BuildingManagement.Security.DenyAllBuildingScopedWriteBoundary>(
            writeBoundary);
        var enteredMutation = false;
        await Assert.ThrowsAsync<Volo.Abp.Authorization.AbpAuthorizationException>(() =>
            writeBoundary.ExecuteBuildingAsync(Guid.NewGuid(), () =>
            {
                enteredMutation = true;
                return Task.FromResult(true);
            }));
        Assert.False(enteredMutation);
    }

    [Fact]
    public async Task APIAUTO08_All_Fifteen_Building_Floor_Room_Actions_Stay_Protected()
    {
        await using var app = await StartIsolatedAbpHostAsync();
        var actions = GetGeneratedActions(app);

        foreach (var controller in new[]
        {
            typeof(BuildingManagement.Buildings.BuildingAppService),
            typeof(BuildingManagement.Floors.FloorAppService),
            typeof(BuildingManagement.Rooms.RoomAppService)
        })
        {
            foreach (var method in new[]
                { "CreateAsync", "GetAsync", "GetListAsync", "UpdateAsync", "DeleteAsync" })
            {
                var action = Assert.Single(actions, item =>
                    item.ControllerTypeInfo.AsType() == controller &&
                    item.MethodInfo.Name == method);
                Assert.Contains(action.FilterDescriptors, descriptor =>
                    descriptor.Filter is AuthorizeFilter filter &&
                    filter.Policy is not null &&
                    filter.Policy.Requirements.Any(requirement =>
                        requirement is DenyAnonymousAuthorizationRequirement));
            }
        }

        // Authorization must be consumed by AppServices, never become
        // an automatically generated permission-checking HTTP route.
        Assert.DoesNotContain(actions, action =>
            action.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Security.BuildingCrudAccessPolicy));
        var attribute = (Volo.Abp.RemoteServiceAttribute?)
            Attribute.GetCustomAttribute(
                typeof(BuildingManagement.Security.BuildingCrudAccessPolicy),
                typeof(Volo.Abp.RemoteServiceAttribute));
        Assert.NotNull(attribute);
        Assert.False(attribute.IsEnabled);
    }

    [Fact]
    public async Task APIAUTO09_Authenticated_Unassigned_User_Cannot_List_Any_Building_Floor_Room()
    {
        await using var app = await StartIsolatedAbpHostAsync();
        using var client = AuthenticatedClient(app);

        foreach (var service in new[]
            { "BuildingAppService", "FloorAppService", "RoomAppService" })
        {
            var action = Assert.Single(GetGeneratedActions(app), x =>
                x.ControllerTypeInfo.Name == service &&
                x.MethodInfo.Name == "GetListAsync");
            var route = Assert.IsType<string>(action.AttributeRouteInfo?.Template);
            using var response = await client.GetAsync("/" + route.TrimStart('/'));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task APIAUTO10_Authenticated_Unassigned_User_Cannot_Read_Guessed_Resource_Ids()
    {
        await using var app = await StartIsolatedAbpHostAsync();
        using var client = AuthenticatedClient(app);

        foreach (var service in new[]
            { "BuildingAppService", "FloorAppService", "RoomAppService" })
        {
            var action = Assert.Single(GetGeneratedActions(app), x =>
                x.ControllerTypeInfo.Name == service &&
                x.MethodInfo.Name == "GetAsync");
            var route = Assert.IsType<string>(action.AttributeRouteInfo?.Template);
            var withId = Regex.Replace(route, @"\{[^}]+\}",
                Guid.NewGuid().ToString("D"));
            using var response = await client.GetAsync("/" + withId.TrimStart('/'));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task APIAUTO11_Authenticated_Unassigned_User_Cannot_Create_Child_Resources()
    {
        await using var app = await StartIsolatedAbpHostAsync();
        using var client = AuthenticatedClient(app);

        foreach (var (service, payload) in new[]
        {
            ("FloorAppService", "{\"buildingId\":\"" + Guid.NewGuid() +
                "\",\"floorNumber\":1,\"name\":\"Blocked floor\"}"),
            ("RoomAppService", "{\"floorId\":\"" + Guid.NewGuid() +
                "\",\"roomNumber\":\"101\",\"name\":\"Blocked room\",\"capacity\":2,\"status\":0}")
        })
        {
            var action = Assert.Single(GetGeneratedActions(app), x =>
                x.ControllerTypeInfo.Name == service &&
                x.MethodInfo.Name == "CreateAsync");
            var route = Assert.IsType<string>(action.AttributeRouteInfo?.Template);
            using var body = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/" + route.TrimStart('/'), body);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task APIAUTO12_Authenticated_User_Cannot_Create_Orphan_Building()
    {
        await using var app = await StartIsolatedAbpHostAsync();
        using var client = AuthenticatedClient(app);
        var action = Assert.Single(GetGeneratedActions(app), x =>
            x.ControllerTypeInfo.AsType() == typeof(BuildingAppService) &&
            x.MethodInfo.Name == "CreateAsync");
        var route = Assert.IsType<string>(action.AttributeRouteInfo?.Template);
        using var body = new StringContent(
            "{\"name\":\"Blocked building\",\"address\":\"No owner bootstrap\"}",
            Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/" + route.TrimStart('/'), body);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task APIAUTO14_Authenticated_User_Cannot_Update_Or_Delete_Any_Of_Three_Resources()
    {
        await using var app = await StartIsolatedAbpHostAsync();
        using var client = AuthenticatedClient(app);
        var actions = GetGeneratedActions(app);
        var guessed = Guid.NewGuid().ToString("D");

        // Previously checked GET/list/create, but not the 6 HTTP mutations.
        // Exercise the actual generated endpoints for EACH service. All must
        // fail at the permission+membership policy, not reach repositories.
        foreach (var (service, payload) in new[]
        {
            ("BuildingAppService",
                "{\"name\":\"Denied update\",\"address\":\"No access\"}"),
            ("FloorAppService",
                "{\"floorNumber\":1,\"name\":\"Denied update\"}"),
            ("RoomAppService",
                "{\"roomNumber\":\"101\",\"name\":\"Denied update\"," +
                "\"area\":25,\"capacity\":2,\"status\":0}")
        })
        {
            foreach (var method in new[] { "UpdateAsync", "DeleteAsync" })
            {
                var action = Assert.Single(actions, a =>
                    a.ControllerTypeInfo.Name == service &&
                    a.MethodInfo.Name == method);
                var route = Assert.IsType<string>(
                    action.AttributeRouteInfo?.Template);
                Assert.Contains("{", route); // route must carry resource ID
                var url = "/" + Regex.Replace(route,
                    @"\{[^}]+\}", guessed).TrimStart('/');

                using var request = method == "UpdateAsync"
                    ? new HttpRequestMessage(HttpMethod.Put, url)
                    {
                        Content = new StringContent(payload, Encoding.UTF8,
                            "application/json")
                    }
                    : new HttpRequestMessage(HttpMethod.Delete, url);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task APIAUTO15_Spoofed_Admin_Or_Building_Headers_Do_Not_Grant_Scope()
    {
        await using var app = await StartIsolatedAbpHostAsync();
        using var client = AuthenticatedClient(app);
        client.DefaultRequestHeaders.Add("X-Is-Admin", "true");
        client.DefaultRequestHeaders.Add("X-Building-Id",
            Guid.NewGuid().ToString("D"));
        client.DefaultRequestHeaders.Add("X-Permission",
            "BuildingManagement.Buildings.Manage");

        var action = Assert.Single(GetGeneratedActions(app), a =>
            a.ControllerTypeInfo.AsType() == typeof(BuildingAppService) &&
            a.MethodInfo.Name == nameof(BuildingAppService.GetListAsync));
        var route = Assert.IsType<string>(action.AttributeRouteInfo?.Template);
        using var response = await client.GetAsync("/" + route.TrimStart('/'));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task APIAUTO16_Authenticated_SliceB_Five_Service_Groups_Deny_Before_DI()
    {
        await using var app = await StartIsolatedAbpHostAsync();
        using var client = AuthenticatedClient(app);
        // Spoofed headers must not convert authentication into a per-building
        // entitlement. The in-memory host has NO repositories or real DB.
        client.DefaultRequestHeaders.Add("X-Is-Admin", "true");
        client.DefaultRequestHeaders.Add("X-Building-Id",
            Guid.NewGuid().ToString("D"));

        var serviceTypes = new[]
        {
            typeof(BuildingManagement.Contracts.ContractAppService),
            typeof(BuildingManagement.Contracts.ContractRenewalHoldAppService),
            typeof(BuildingManagement.Tenants.TenantAppService),
            typeof(BuildingManagement.RoomReservations.RoomReservationAppService),
            typeof(BuildingManagement.MoveOuts.MoveOutRequestAppService)
        };
        var actions = GetGeneratedActions(app);
        foreach (var service in serviceTypes)
        {
            var serviceActions = actions.Where(action =>
                action.ControllerTypeInfo.AsType() == service).ToArray();
            Assert.NotEmpty(serviceActions);
            foreach (var action in serviceActions)
            {
                Assert.Contains(action.FilterDescriptors, descriptor =>
                    descriptor.Filter is AuthorizeFilter filter &&
                    filter.Policy is not null &&
                    filter.Policy.Requirements.Any(requirement =>
                        requirement is Microsoft.AspNetCore.Authorization.Infrastructure
                            .AssertionRequirement));
            }

            // Authenticated GET/list calls must fail before resolving the
            // underlying service, repository, tenant PII or contract state.
            var method = service == typeof(BuildingManagement.MoveOuts.MoveOutRequestAppService)
                ? "GetByContractAsync" : "GetListAsync";
            var list = Assert.Single(serviceActions, action =>
                action.MethodInfo.Name == method);
            var route = Assert.IsType<string>(list.AttributeRouteInfo?.Template);
            var url = "/" + Regex.Replace(route, @"\{[^}]+\}",
                Guid.NewGuid().ToString("D")).TrimStart('/');
            using var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // An arbitrary known tenant ID must not reveal personal details.
        var tenantRead = Assert.Single(actions, action =>
            action.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Tenants.TenantAppService) &&
            action.MethodInfo.Name == "GetAsync");
        var tenantRoute = Assert.IsType<string>(
            tenantRead.AttributeRouteInfo?.Template);
        using var result = await client.GetAsync("/" + Regex.Replace(
            tenantRoute, @"\{[^}]+\}", Guid.NewGuid().ToString("D"))
            .TrimStart('/'));
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
    }

    private static HttpClient AuthenticatedClient(WebApplication app)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(
            "X-Isolated-Security-User", Guid.NewGuid().ToString("D"));
        return client;
    }

    [Fact]
    public async Task APIAUTO13_Direct_Contract_End_And_MoveOut_Close_Preflight_Are_Not_Remote()
    {
        await using var app = await StartIsolatedAbpHostAsync();
        // ABP MVC may retain a ControllerActionDescriptor for a disabled
        // method with AttributeRouteInfo == null. That is NOT an exposed HTTP
        // endpoint. Inspect EndpointDataSource (the actual routable surface)
        // instead of requiring its discovery descriptor to disappear.
        var exposed = app.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Select(endpoint =>
                endpoint.Metadata.GetMetadata<ControllerActionDescriptor>())
            .Where(action => action is not null)
            .ToArray();

        Assert.NotEmpty(exposed);
        // The forbidden action must disappear from BOTH MVC discovery and
        // actual routing. Merely setting AttributeRouteInfo=null was not
        // sufficient: endpoint routing still included EndAsync.
        var discovered = GetGeneratedActions(app);
        Assert.DoesNotContain(discovered, action =>
            action.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Contracts.ContractAppService) &&
            action.MethodInfo.Name == "EndAsync");
        Assert.Contains(exposed, action =>
            action!.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Contracts.ContractAppService) &&
            action.MethodInfo.Name == "GetAsync");

        // No routable direct EndAsync command may bypass the final invoice,
        // debt settlement, deposit and move-out handover validations.
        Assert.DoesNotContain(exposed, action =>
            action!.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Contracts.ContractAppService) &&
            action.MethodInfo.Name == "EndAsync");

        // The readiness preflight remains internal-only until real scoped
        // authorization is available for Tenant / Owner / Staff.
        Assert.DoesNotContain(exposed, action =>
            action!.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.MoveOuts.MoveOutCompletionReadinessAppService));

        // Meter checklist mutates evidence-backed state, and must stay
        // outside HTTP until scoped staff/owner authority is activated.
        Assert.DoesNotContain(exposed, action =>
            action!.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.MoveOuts.MoveOutFinalMeterSignoffAppService));

        // Final bill/rent financial previews are deliberately internal;
        // a persisted utility DRAFT is not a public payable invoice.
        Assert.DoesNotContain(exposed, action =>
            action!.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Invoices.FinalMoveOutBillReviewAppService));
        Assert.DoesNotContain(exposed, action =>
            action!.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Invoices.FinalInvoiceEvidencePackageAppService));
        Assert.DoesNotContain(exposed, action =>
            action!.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Invoices.FinalInvoiceIssuePreflightAppService));
        Assert.DoesNotContain(exposed, action =>
            action!.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Invoices.FinalInvoiceIssueEvidenceDriftAppService));
        Assert.DoesNotContain(exposed, action =>
            action!.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Invoices.InvoicePaymentAllocationLedgerReviewAppService));
        Assert.DoesNotContain(exposed, action =>
            action!.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Invoices.FinalInvoiceReleaseReadinessAppService));
        Assert.DoesNotContain(exposed, action =>
            action!.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Invoices.FinalInvoiceSourceProvenanceAppService));
        Assert.DoesNotContain(exposed, action =>
            action!.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Invoices.FinalMoveOutPaymentEvidenceAppService));
        Assert.DoesNotContain(exposed, action =>
            action!.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.DepositSettlements.ContractDepositLedgerReviewAppService));
        Assert.DoesNotContain(exposed, action =>
            action!.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Invoices.FinalMonthRentPreviewAppService));

        var gate = app.Services.GetRequiredService<
            BuildingManagement.Contracts.IDirectContractTerminationGate>();
        Assert.IsType<
            BuildingManagement.Contracts.DenyAllDirectContractTerminationGate>(gate);
        await Assert.ThrowsAsync<Volo.Abp.Authorization.AbpAuthorizationException>(
            () => gate.EnsureDirectTerminationAllowedAsync());
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

            // Test-only repository proxies ensure that an authorized HTTP
            // request can instantiate actual AppServices, but any accidental
            // database access before permission checks fails the test.
            // This is NOT a production service registration.
            context.Services.AddSingleton<IRepository<BuildingManagement.Buildings.Building, Guid>>(
                DispatchProxy.Create<
                    IRepository<BuildingManagement.Buildings.Building, Guid>,
                    NoDatabaseRepositoryProxy>());
            context.Services.AddSingleton<IRepository<BuildingManagement.Floors.Floor, Guid>>(
                DispatchProxy.Create<
                    IRepository<BuildingManagement.Floors.Floor, Guid>,
                    NoDatabaseRepositoryProxy>());
            context.Services.AddSingleton<IRepository<BuildingManagement.Rooms.Room, Guid>>(
                DispatchProxy.Create<
                    IRepository<BuildingManagement.Rooms.Room, Guid>,
                    NoDatabaseRepositoryProxy>());
            context.Services.AddSingleton<IRepository<BuildingManagement.Contracts.Contract, Guid>>(
                DispatchProxy.Create<
                    IRepository<BuildingManagement.Contracts.Contract, Guid>,
                    NoDatabaseRepositoryProxy>());
            context.Services.AddSingleton<IRepository<BuildingManagement.Utilities.UtilityMeter, Guid>>(
                DispatchProxy.Create<
                    IRepository<BuildingManagement.Utilities.UtilityMeter, Guid>,
                    NoDatabaseRepositoryProxy>());

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
        {
            if (!Request.Headers.TryGetValue("X-Isolated-Security-User", out var raw) ||
                !Guid.TryParse(raw.ToString(), out var id) || id == Guid.Empty)
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, id.ToString("D")),
                    new Claim(ClaimTypes.Name, "isolated-security-test")
                },
                Scheme.Name);
            var ticket = new AuthenticationTicket(
                new ClaimsPrincipal(identity), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
    public class NoDatabaseRepositoryProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => throw new InvalidOperationException(
                "SECURITY TEST FAILURE: repository accessed before current building grant authorization.");
    }

}
