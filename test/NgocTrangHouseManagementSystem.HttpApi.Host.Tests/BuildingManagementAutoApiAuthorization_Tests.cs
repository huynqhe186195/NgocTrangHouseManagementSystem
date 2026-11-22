using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using BuildingManagement;
using BuildingManagement.Buildings;
using BuildingManagement.Contracts;
using BuildingManagement.Floors;
using BuildingManagement.Invoices;
using BuildingManagement.RentalAccounts;
using BuildingManagement.Rooms;
using BuildingManagement.Tenants;
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
using Volo.Abp.Security.Claims;
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
    public async Task APIAUTO09_Authenticated_Unassigned_User_Cannot_List_Any_Building_Floor_Room()    {
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
    public async Task APIAUTO15_Building_Public_Info_Read_And_Publish_Fail_Closed_Without_A_Grant()
    {
        await using var app = await StartIsolatedAbpHostAsync();
        using var client = AuthenticatedClient(app);
        var actions = GetGeneratedActions(app);
        var guessed = Guid.NewGuid().ToString("D");

        // The endpoint left the default-deny list because it enforces its own
        // per-building policy. That only holds if it really refuses an
        // authenticated user who has no grant — the exact case login alone
        // must never unlock.
        var get = Assert.Single(actions, x =>
            x.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Buildings.BuildingPublicInfoAppService) &&
            x.MethodInfo.Name == "GetAsync");
        var getRoute = Assert.IsType<string>(get.AttributeRouteInfo?.Template);
        using var getResponse = await client.GetAsync(
            "/" + Regex.Replace(getRoute, @"\{[^}]+\}", guessed).TrimStart('/'));
        Assert.Equal(HttpStatusCode.Forbidden, getResponse.StatusCode);

        var update = Assert.Single(actions, x =>
            x.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Buildings.BuildingPublicInfoAppService) &&
            x.MethodInfo.Name == "UpdateAsync");
        var updateRoute = Assert.IsType<string>(update.AttributeRouteInfo?.Template);
        using var body = new StringContent(
            "{\"electricityUnitPrice\":3500}", Encoding.UTF8, "application/json");
        using var updateResponse = await client.PutAsync(
            "/" + Regex.Replace(updateRoute, @"\{[^}]+\}", guessed).TrimStart('/'), body);
        Assert.Equal(HttpStatusCode.Forbidden, updateResponse.StatusCode);
    }

    [Fact]
    public async Task APIAUTO12_Authenticated_User_Cannot_Create_Orphan_Building()    {
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
    public async Task APIAUTO16_Authenticated_SliceB_Six_Service_Groups_Deny_Before_DI()
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
            typeof(BuildingManagement.MoveOuts.MoveOutRequestAppService),
            typeof(BuildingManagement.Payments.PaymentAppService)
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

        // Existing legacy contract create/update remain denied even for
        // authenticated callers. Only the isolated staging controller has
        // reviewed POST/PUT endpoints.
        var legacyCreate = Assert.Single(actions, action =>
            action.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Contracts.ContractAppService) &&
            action.MethodInfo.Name == "CreateAsync");
        using (var body = new StringContent("{}", Encoding.UTF8, "application/json"))
        using (var response = await client.PostAsync(
            "/" + Assert.IsType<string>(legacyCreate.AttributeRouteInfo?.Template)
                .TrimStart('/'), body))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // A forged authenticated caller cannot create arbitrary deposit
        // records via legacy PaymentAppService, even without a database.
        var legacyPaymentCreate = Assert.Single(actions, action =>
            action.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Payments.PaymentAppService) &&
            action.MethodInfo.Name == "CreateReservationDepositAsync");
        using (var body = new StringContent("{}", Encoding.UTF8, "application/json"))
        using (var response = await client.PostAsync(
            "/" + Assert.IsType<string>(
                legacyPaymentCreate.AttributeRouteInfo?.Template)
                .TrimStart('/'), body))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

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

    private static HttpClient AuthenticatedClient(WebApplication app, Guid actor)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(
            "X-Isolated-Security-User", actor.ToString("D"));
        return client;
    }

    private static HttpClient AuthenticatedClient(WebApplication app)
        => AuthenticatedClient(app, Guid.NewGuid());

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

    /// <summary>
    /// The read-only contract list left the default-deny list because both of
    /// its methods enforce the per-building policy themselves. That only holds
    /// if an authenticated caller WITHOUT a grant is refused: production's
    /// operator readers deny everyone today, so a 403 here is the shipped
    /// behaviour, not a regression.
    /// </summary>
    [Fact]
    public async Task APIAUTO17_Contract_List_Stays_Closed_Without_A_Building_Grant()
    {
        await using var app = await StartIsolatedAbpHostAsync();
        using var client = AuthenticatedClient(app);
        // Spoofed entitlement headers must not substitute for a real grant.
        client.DefaultRequestHeaders.Add("X-Is-Admin", "true");
        client.DefaultRequestHeaders.Add("X-Building-Id",
            Guid.NewGuid().ToString("D"));
        client.DefaultRequestHeaders.Add("X-Permission",
            "BuildingManagement.Contracts.Read");

        var actions = GetGeneratedActions(app);
        var guessed = Guid.NewGuid().ToString("D");

        // The allowlist entry is safe only while this service carries nothing
        // but reads. Adding a write here would expose it over HTTP with every
        // other test still green, so the surface itself is pinned.
        var contractSurface = actions
            .Where(action => action.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Contracts.ContractsAppService))
            .ToArray();
        Assert.Equal(2, contractSurface.Length);
        Assert.Equal(
            new[] { "GetAsync", "GetListAsync" },
            contractSurface.Select(action => action.MethodInfo.Name).OrderBy(x => x));

        var list = Assert.Single(actions, action =>
            action.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Contracts.ContractsAppService) &&
            action.MethodInfo.Name == "GetListAsync");
        var listRoute = Assert.IsType<string>(list.AttributeRouteInfo?.Template);
        Assert.Equal("api/building-management/contracts", listRoute);
        using (var response = await client.GetAsync(
            "/" + listRoute.TrimStart('/')))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // A known-shaped id must not distinguish itself either: the policy runs
        // before any contract is loaded.
        var get = Assert.Single(actions, action =>
            action.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Contracts.ContractsAppService) &&
            action.MethodInfo.Name == "GetAsync");
        var getRoute = Assert.IsType<string>(get.AttributeRouteInfo?.Template);
        Assert.Equal("api/building-management/contracts/{id}", getRoute);
        using (var response = await client.GetAsync(
            "/" + Regex.Replace(getRoute, @"\{[^}]+\}", guessed)
                .TrimStart('/')))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // The "contracts" segment comes from the class name ContractsAppService
        // (ABP only kebab-cases the type name; it never singularises), so it sits
        // beside RoomAppService's room/{id}/contracts rather than shadowing it.
        var roomContracts = Assert.Single(actions, action =>
            action.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Rooms.RoomAppService) &&
            action.MethodInfo.Name == "GetContractsAsync");
        var roomRoute = Assert.IsType<string>(
            roomContracts.AttributeRouteInfo?.Template);
        Assert.Equal("api/building-management/room/{id}/contracts", roomRoute);
        Assert.NotEqual(
            listRoute.TrimStart('/').ToLowerInvariant(),
            roomRoute.TrimStart('/').ToLowerInvariant());
    }

    /// <summary>
    /// Same argument as APIAUTO17, for the roster. A tenant carries citizen id,
    /// address and emergency contact, so the read is its own grant rather than
    /// an implication of Contracts.Read. TenantAppService — which returns every
    /// tenant unscoped and also writes — must stay closed.
    /// </summary>
    [Fact]
    public async Task APIAUTO18_Tenant_Roster_Stays_Closed_Without_A_Building_Grant()
    {
        await using var app = await StartIsolatedAbpHostAsync();
        using var client = AuthenticatedClient(app);
        client.DefaultRequestHeaders.Add("X-Is-Admin", "true");
        client.DefaultRequestHeaders.Add("X-Building-Id",
            Guid.NewGuid().ToString("D"));
        client.DefaultRequestHeaders.Add("X-Permission",
            "BuildingManagement.Tenants.Read");

        var actions = GetGeneratedActions(app);

        // Read-only by construction. A write added here would go out over HTTP
        // with every other test still green, so the surface itself is pinned.
        var rosterSurface = actions
            .Where(action => action.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Tenants.TenantsAppService))
            .ToArray();
        Assert.Equal(2, rosterSurface.Length);
        Assert.Equal(
            new[] { "GetAsync", "GetListAsync" },
            rosterSurface.Select(action => action.MethodInfo.Name).OrderBy(x => x));

        var list = Assert.Single(rosterSurface, action =>
            action.MethodInfo.Name == "GetListAsync");
        var listRoute = Assert.IsType<string>(list.AttributeRouteInfo?.Template);
        // ABP kebab-cases the type name and does not singularise, so this sits
        // beside TenantAppService's "tenant" route rather than shadowing it.
        Assert.Equal("api/building-management/tenants", listRoute);
        using (var response = await client.GetAsync("/" + listRoute.TrimStart('/')))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var get = Assert.Single(rosterSurface, action =>
            action.MethodInfo.Name == "GetAsync");
        var getRoute = Assert.IsType<string>(get.AttributeRouteInfo?.Template);
        Assert.Equal("api/building-management/tenants/{id}", getRoute);
        using (var response = await client.GetAsync(
            "/" + Regex.Replace(getRoute, @"\{[^}]+\}",
                Guid.NewGuid().ToString("D")).TrimStart('/')))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // The unscoped write service must not share the roster's route.
        var legacyTenant = actions
            .Where(action => action.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Tenants.TenantAppService))
            .Select(action => Assert.IsType<string>(
                action.AttributeRouteInfo?.Template))
            .ToArray();
        Assert.NotEmpty(legacyTenant);
        Assert.All(legacyTenant, route =>
            Assert.False(route.TrimStart('/').Equals(
                listRoute.TrimStart('/'), StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task APIAUTO19_Operator_Membership_Roster_Is_Read_Only_And_Fail_Closed()
    {
        await using var app = await StartIsolatedAbpHostAsync();
        var actions = GetGeneratedActions(app);
        var surface = actions
            .Where(action => action.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.OperatorMemberships.OperatorMembershipsAppService))
            .ToArray();

        Assert.Single(surface);
        var action = Assert.Single(surface, item =>
            item.MethodInfo.Name == nameof(
                BuildingManagement.OperatorMemberships.IOperatorMembershipsAppService.GetListAsync));
        var route = Assert.IsType<string>(action.AttributeRouteInfo?.Template);
        Assert.Equal("api/building-management/operator-memberships", route);
        Assert.Contains(action.FilterDescriptors, descriptor =>
            descriptor.Filter is AuthorizeFilter filter &&
            filter.Policy is not null &&
            filter.Policy.Requirements.Any(requirement =>
                requirement is DenyAnonymousAuthorizationRequirement));

        Assert.DoesNotContain(surface, item =>
            item.MethodInfo.Name.Contains("Assign", StringComparison.OrdinalIgnoreCase) ||
            item.MethodInfo.Name.Contains("Revoke", StringComparison.OrdinalIgnoreCase));

        var store = app.Services.GetRequiredService<
            BuildingManagement.OperatorMemberships.IOperatorMembershipReadStore>();
        Assert.IsType<BuildingManagement.OperatorMemberships.DenyAllOperatorMembershipReadStore>(store);

        using var anonymous = app.GetTestClient();
        using (var response = await anonymous.GetAsync("/" + route.TrimStart('/')))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var authenticated = AuthenticatedClient(app);
        using (var response = await authenticated.GetAsync(
            "/" + route.TrimStart('/')))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task APIMOBILE01_RentalAccount_Me_Is_Explicit_Authenticated_Route()
    {
        await using var app = await StartIsolatedAbpHostAsync();

        var endpoint = Assert.Single(
            app.Services.GetRequiredService<EndpointDataSource>()
                .Endpoints.OfType<RouteEndpoint>()
                .Where(item => item.Metadata
                    .GetMetadata<ControllerActionDescriptor>() is
                    { ControllerTypeInfo: var controllerType,
                      MethodInfo.Name: nameof(BuildingManagement.HttpApi.Mobile.RentalAccountController.GetMeAsync) }
                    && controllerType.AsType() ==
                        typeof(BuildingManagement.HttpApi.Mobile.RentalAccountController)));
        var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
        Assert.NotNull(action);
        Assert.Equal("api/mobile/rental-account/me",
            action!.AttributeRouteInfo?.Template);
        Assert.DoesNotContain("{", action.AttributeRouteInfo?.Template);
        Assert.DoesNotContain(endpoint.Metadata,
            metadata => metadata is IAllowAnonymous);

        var actionDescriptors = GetGeneratedActions(app);
        Assert.DoesNotContain(actionDescriptors, item =>
            item.ControllerTypeInfo.AsType() ==
                typeof(BuildingManagement.Application.Mobile.MobileTenantAppService));

        var mobileEndpoints = app.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Where(item => item.Metadata.GetMetadata<ControllerActionDescriptor>() is
                { ControllerTypeInfo: var controllerType } &&
                controllerType.AsType() ==
                    typeof(BuildingManagement.HttpApi.Mobile.RentalAccountController))
            .ToArray();
        Assert.Single(mobileEndpoints);

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/mobile/rental-account/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    [Fact]
    public async Task APIMOBILE02_Mapped_Active_User_Gets_Own_Rental_Account_Graph()
    {
        var fixture = MobileScopeFixture.Create();
        await using var app = await StartIsolatedAbpHostAsync(fixture);
        using var client = AuthenticatedClient(app, fixture.UserA);
        using var response = await client.GetAsync("/api/mobile/rental-account/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(fixture.TenantA, root.GetProperty("currentUser").GetProperty("tenantId").GetGuid());
        Assert.Equal(fixture.AccountA, root.GetProperty("account").GetProperty("id").GetGuid());
        Assert.Equal(fixture.ContractA, root.GetProperty("activeContract").GetProperty("id").GetGuid());
        Assert.Equal(fixture.RoomA, root.GetProperty("activeContract").GetProperty("room").GetProperty("id").GetGuid());
        Assert.Equal(fixture.BuildingA, root.GetProperty("activeContract").GetProperty("room").GetProperty("buildingId").GetGuid());
        Assert.Equal("Tenant A", root.GetProperty("currentUser").GetProperty("displayName").GetString());
        Assert.DoesNotContain("Tenant B private PII", root.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(1, root.GetProperty("members").GetArrayLength());
        Assert.DoesNotContain(root.GetProperty("members").EnumerateArray(), member =>
            member.GetProperty("tenantId").GetGuid() == fixture.TenantB);
        Assert.Equal(1, fixture.AccessLog.Count(x => x.EntityType == typeof(RentalAccountUser) &&
            x.Operation == "get_AsyncExecuter"));
    }

    [Fact]
    public async Task APIMOBILE03_Authenticated_Unmapped_Subject_Returns_403()
    {
        var fixture = MobileScopeFixture.Create();
        await using var app = await StartIsolatedAbpHostAsync(fixture);
        using var client = AuthenticatedClient(app, fixture.UnknownUser);
        using var response = await client.GetAsync("/api/mobile/rental-account/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(fixture.AccessLog, x => x.EntityType == typeof(RentalAccountUser));
        Assert.DoesNotContain(fixture.AccessLog, x => x.EntityType == typeof(RentalAccount));
        Assert.DoesNotContain(fixture.AccessLog, x => x.EntityType == typeof(Contract));
        Assert.DoesNotContain(fixture.AccessLog, x => x.EntityType == typeof(Tenant));
    }

    [Fact]
    public async Task APIMOBILE04_Revoked_RentalAccountUser_Returns_403()
    {
        var fixture = MobileScopeFixture.Create();
        await using var app = await StartIsolatedAbpHostAsync(fixture);
        using var client = AuthenticatedClient(app, fixture.RevokedUser);
        using var response = await client.GetAsync("/api/mobile/rental-account/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(fixture.AccessLog, x => x.EntityType == typeof(RentalAccount));
        Assert.DoesNotContain(fixture.AccessLog, x => x.EntityType == typeof(Tenant));
    }

    [Fact]
    public async Task APIMOBILE05_Inactive_Account_Returns_403()
    {
        var fixture = MobileScopeFixture.Create();
        await using var app = await StartIsolatedAbpHostAsync(fixture);
        using var client = AuthenticatedClient(app, fixture.InactiveAccountUser);
        using var response = await client.GetAsync("/api/mobile/rental-account/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(fixture.AccessLog, x => x.EntityType == typeof(RentalAccount));
        Assert.DoesNotContain(fixture.AccessLog, x => x.EntityType == typeof(Contract));
    }

    [Fact]
    public async Task APIMOBILE06_Spoofed_Authority_Does_Not_Change_Actor_Scope()
    {
        var fixture = MobileScopeFixture.Create();
        await using var app = await StartIsolatedAbpHostAsync(fixture);
        using var client = AuthenticatedClient(app, fixture.UserA);
        client.DefaultRequestHeaders.Add("X-Is-Admin", "true");
        client.DefaultRequestHeaders.Add("X-Permission", "BuildingManagement.RentalAccounts.Read");
        client.DefaultRequestHeaders.Add("X-Rental-Account-Id", fixture.AccountB.ToString("D"));
        client.DefaultRequestHeaders.Add("X-Tenant-Id", fixture.TenantB.ToString("D"));
        client.DefaultRequestHeaders.Add("X-Building-Id", fixture.BuildingB.ToString("D"));

        using var response = await client.GetAsync(
            "/api/mobile/rental-account/me?TenantId=" + fixture.TenantB +
            "&AccountId=" + fixture.AccountB + "&RoomId=" + fixture.RoomB +
            "&ContractId=" + fixture.ContractB);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(fixture.UserA, json.RootElement.GetProperty("currentUser").GetProperty("identityUserId").GetGuid());
        Assert.Equal(fixture.AccountA, json.RootElement.GetProperty("account").GetProperty("id").GetGuid());
        Assert.Equal(fixture.RoomA, json.RootElement.GetProperty("activeContract").GetProperty("room").GetProperty("id").GetGuid());
        Assert.DoesNotContain(fixture.AccountB.ToString("D"), json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.TenantB.ToString("D"), json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task APIMOBILE07_Unknown_User_Cannot_Use_Spoofed_Authority_Headers()
    {
        var fixture = MobileScopeFixture.Create();
        await using var app = await StartIsolatedAbpHostAsync(fixture);
        using var client = AuthenticatedClient(app, fixture.UnknownUser);
        client.DefaultRequestHeaders.Add("X-Is-Admin", "true");
        client.DefaultRequestHeaders.Add("X-Rental-Account-Id", fixture.AccountA.ToString("D"));
        client.DefaultRequestHeaders.Add("X-Tenant-Id", fixture.TenantA.ToString("D"));

        using var response = await client.GetAsync("/api/mobile/rental-account/me");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(fixture.AccessLog, x => x.EntityType == typeof(RentalAccount));
    }

    private static ControllerActionDescriptor[] GetGeneratedActions(WebApplication app)
        => app.Services
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(action => action.ControllerTypeInfo.Assembly ==
                typeof(BuildingManagementApplicationModule).Assembly)
            .ToArray();

    private static async Task<WebApplication> StartIsolatedAbpHostAsync(
        MobileScopeFixture? mobileScopeFixture = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseTestServer();
        builder.Host.UseAutofac();

        await builder.AddApplicationAsync<BuildingManagementApiInventoryTestModule>();
        if (mobileScopeFixture is not null)
        {
            MobileScopeFixture.Register(builder.Services, mobileScopeFixture);
        }

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
            context.Services.AddSingleton<IRepository<BuildingManagement.Buildings.BuildingPhoto, Guid>>(
                DispatchProxy.Create<
                    IRepository<BuildingManagement.Buildings.BuildingPhoto, Guid>,
                    NoDatabaseRepositoryProxy>());
            context.Services.AddSingleton<IRepository<BuildingManagement.Buildings.BuildingPhotoContent, Guid>>(
                DispatchProxy.Create<
                    IRepository<BuildingManagement.Buildings.BuildingPhotoContent, Guid>,
                    NoDatabaseRepositoryProxy>());
            context.Services.AddSingleton<IRepository<BuildingManagement.Buildings.BuildingPublicInfo, Guid>>(
                DispatchProxy.Create<
                    IRepository<BuildingManagement.Buildings.BuildingPublicInfo, Guid>,
                    NoDatabaseRepositoryProxy>());
            context.Services.AddSingleton<IRepository<BuildingManagement.Floors.Floor, Guid>>(
                DispatchProxy.Create<
                    IRepository<BuildingManagement.Floors.Floor, Guid>,
                    NoDatabaseRepositoryProxy>());
            context.Services.AddSingleton<IRepository<BuildingManagement.Rooms.Room, Guid>>(
                DispatchProxy.Create<
                    IRepository<BuildingManagement.Rooms.Room, Guid>,
                    NoDatabaseRepositoryProxy>());
            context.Services.AddSingleton<IRepository<BuildingManagement.Rooms.RoomEquipment, Guid>>(
                DispatchProxy.Create<
                    IRepository<BuildingManagement.Rooms.RoomEquipment, Guid>,
                    NoDatabaseRepositoryProxy>());
            context.Services.AddSingleton<IRepository<BuildingManagement.Rooms.RoomPhoto, Guid>>(
                DispatchProxy.Create<
                    IRepository<BuildingManagement.Rooms.RoomPhoto, Guid>,
                    NoDatabaseRepositoryProxy>());
            context.Services.AddSingleton<IRepository<BuildingManagement.Rooms.RoomPhotoContent, Guid>>(
                DispatchProxy.Create<
                    IRepository<BuildingManagement.Rooms.RoomPhotoContent, Guid>,
                    NoDatabaseRepositoryProxy>());
            context.Services.AddSingleton<IRepository<BuildingManagement.Floors.FloorLayoutItem, Guid>>(
                DispatchProxy.Create<
                    IRepository<BuildingManagement.Floors.FloorLayoutItem, Guid>,
                    NoDatabaseRepositoryProxy>());
            context.Services.AddSingleton<IRepository<BuildingManagement.Contracts.Contract, Guid>>(
                DispatchProxy.Create<
                    IRepository<BuildingManagement.Contracts.Contract, Guid>,
                    NoDatabaseRepositoryProxy>());
            context.Services.AddSingleton<IRepository<BuildingManagement.Contracts.ContractTenant, Guid>>(
                DispatchProxy.Create<
                    IRepository<BuildingManagement.Contracts.ContractTenant, Guid>,
                    NoDatabaseRepositoryProxy>());
            context.Services.AddSingleton<IRepository<BuildingManagement.Tenants.Tenant, Guid>>(
                DispatchProxy.Create<
                    IRepository<BuildingManagement.Tenants.Tenant, Guid>,
                    NoDatabaseRepositoryProxy>());
            context.Services.AddSingleton<IRepository<BuildingManagement.Utilities.UtilityMeter, Guid>>(
                DispatchProxy.Create<
                    IRepository<BuildingManagement.Utilities.UtilityMeter, Guid>,
                    NoDatabaseRepositoryProxy>());

            // Mobile scope repositories are replaced by MobileScopeFixture only
            // for the explicit HTTP scope tests. The default host remains a
            // database-free deny-first authorization harness.

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
                    new Claim(AbpClaimTypes.UserId, id.ToString("D")),
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

    private sealed class MobileScopeFixture
    {
        private readonly List<RentalAccountUser> _accountUsers = new();
        private readonly List<RentalAccount> _accounts = new();
        private readonly List<Contract> _contracts = new();
        private readonly List<Room> _rooms = new();
        private readonly List<Floor> _floors = new();
        private readonly List<Building> _buildings = new();
        private readonly List<ContractTenant> _contractTenants = new();
        private readonly List<Tenant> _tenants = new();

        public Guid UserA { get; } = Guid.NewGuid();
        public Guid UserB { get; } = Guid.NewGuid();
        public Guid UnknownUser { get; } = Guid.NewGuid();
        public Guid RevokedUser { get; } = Guid.NewGuid();
        public Guid InactiveAccountUser { get; } = Guid.NewGuid();
        public Guid AccountA { get; } = Guid.NewGuid();
        public Guid AccountB { get; } = Guid.NewGuid();
        public Guid InactiveAccount { get; } = Guid.NewGuid();
        public Guid RoomA { get; } = Guid.NewGuid();
        public Guid RoomB { get; } = Guid.NewGuid();
        public Guid BuildingA { get; } = Guid.NewGuid();
        public Guid BuildingB { get; } = Guid.NewGuid();
        public Guid ContractA { get; } = Guid.NewGuid();
        public Guid ContractB { get; } = Guid.NewGuid();
        public Guid TenantA { get; } = Guid.NewGuid();
        public Guid TenantB { get; } = Guid.NewGuid();
        public List<RepositoryAccess> AccessLog { get; } = new();

        private MobileScopeFixture()
        {
            var floorA = Guid.NewGuid();
            var floorB = Guid.NewGuid();
            var activeLinkA = Guid.NewGuid();
            var activeLinkB = Guid.NewGuid();
            var revokedLink = Guid.NewGuid();
            var inactiveAccountLink = Guid.NewGuid();
            var contractTenantA = Guid.NewGuid();
            var contractTenantB = Guid.NewGuid();

            _buildings.Add(new Building(BuildingA, "Building A", "Address A"));
            _buildings.Add(new Building(BuildingB, "Building B", "Address B"));
            _floors.Add(new Floor(floorA, BuildingA, 1, "Floor A"));
            _floors.Add(new Floor(floorB, BuildingB, 2, "Floor B"));
            _rooms.Add(new Room(RoomA, floorA, "A-101", "Room A", null, 2, RoomStatus.Occupied));
            _rooms.Add(new Room(RoomB, floorB, "B-202", "Room B", null, 2, RoomStatus.Occupied));

            _tenants.Add(new Tenant(TenantA, "Tenant A", "0900000001", "a@example.test",
                null, null, IdentityType.CitizenId, "111111111111"));
            _tenants.Add(new Tenant(TenantB, "Tenant B private PII", "0900000002", "b@example.test",
                null, null, IdentityType.CitizenId, "222222222222"));

            _accounts.Add(new RentalAccount(AccountA, "ACC-A", "Account A"));
            _accounts.Add(new RentalAccount(AccountB, "ACC-B", "Account B"));
            var inactive = new RentalAccount(InactiveAccount, "ACC-INACTIVE", "Inactive Account");
            inactive.Deactivate();
            _accounts.Add(inactive);

            _accountUsers.Add(new RentalAccountUser(activeLinkA, AccountA, UserA, TenantA,
                RentalAccountAccessRole.PrimaryTenant));
            _accountUsers.Add(new RentalAccountUser(activeLinkB, AccountB, UserB, TenantB,
                RentalAccountAccessRole.PrimaryTenant));
            var revoked = new RentalAccountUser(revokedLink, AccountA, RevokedUser, TenantA,
                RentalAccountAccessRole.MemberTenant);
            revoked.Revoke(new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc));
            _accountUsers.Add(revoked);
            _accountUsers.Add(new RentalAccountUser(inactiveAccountLink, InactiveAccount,
                InactiveAccountUser, null, RentalAccountAccessRole.Viewer));

            var contractA = new Contract(ContractA, "CON-A", RoomA,
                new DateTime(2026, 1, 1), null, 5_000_000m);
            contractA.Activate();
            contractA.AssignRentalAccount(AccountA);
            _contracts.Add(contractA);
            _contractTenants.Add(new ContractTenant(contractTenantA, ContractA, TenantA,
                ContractTenantRole.PrimaryTenant));

            var contractB = new Contract(ContractB, "CON-B", RoomB,
                new DateTime(2026, 1, 1), null, 6_000_000m);
            contractB.Activate();
            contractB.AssignRentalAccount(AccountB);
            _contracts.Add(contractB);
            _contractTenants.Add(new ContractTenant(contractTenantB, ContractB, TenantB,
                ContractTenantRole.PrimaryTenant));
        }

        public static MobileScopeFixture Create() => new();

        public static void Register(IServiceCollection services, MobileScopeFixture fixture)
        {
            services.AddSingleton(fixture);
            services.AddSingleton<IRepository<RentalAccountUser, Guid>>(
                fixture.CreateRepository(fixture._accountUsers));
            services.AddSingleton<IRepository<RentalAccount, Guid>>(
                fixture.CreateRepository(fixture._accounts));
            services.AddSingleton<IRepository<Contract, Guid>>(
                fixture.CreateRepository(fixture._contracts));
            services.AddSingleton<IRepository<Room, Guid>>(
                fixture.CreateRepository(fixture._rooms));
            services.AddSingleton<IRepository<Floor, Guid>>(
                fixture.CreateRepository(fixture._floors));
            services.AddSingleton<IRepository<Building, Guid>>(
                fixture.CreateRepository(fixture._buildings));
            services.AddSingleton<IRepository<ContractTenant, Guid>>(
                fixture.CreateRepository(fixture._contractTenants));
            services.AddSingleton<IRepository<Tenant, Guid>>(
                fixture.CreateRepository(fixture._tenants));
        }

        private IRepository<TEntity, Guid> CreateRepository<TEntity>(List<TEntity> rows)
            where TEntity : class, IEntity<Guid>
        {
            var repository = DispatchProxy.Create<
                IRepository<TEntity, Guid>, InMemoryRepositoryProxy<TEntity>>();
            ((InMemoryRepositoryProxy<TEntity>)(object)repository)
                .Initialize(rows, AccessLog);
            return repository;
        }

        public sealed record RepositoryAccess(Type EntityType, string Operation);

        private class InMemoryRepositoryProxy<TEntity> : DispatchProxy
            where TEntity : class, IEntity<Guid>
        {
            private IReadOnlyList<TEntity> _rows = Array.Empty<TEntity>();
            private List<RepositoryAccess> _accessLog = new();

            public void Initialize(List<TEntity> rows, List<RepositoryAccess> accessLog)
            {
                _rows = rows.ToArray();
                _accessLog = accessLog;
            }

            protected override object? Invoke(MethodInfo? method, object?[]? args)
            {
                if (method is null)
                {
                    throw new InvalidOperationException("Repository method was null.");
                }

                var operation = method.Name;
                lock (_accessLog)
                {
                    _accessLog.Add(new RepositoryAccess(typeof(TEntity), operation));
                }

                if (operation == "get_AsyncExecuter")
                {
                    var executerType = method.ReturnType;
                    var proxyType = typeof(AsyncExecuterProxy<>).MakeGenericType(executerType);
                    var create = typeof(DispatchProxy).GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Single(x => x.Name == nameof(DispatchProxy.Create) &&
                                     x.IsGenericMethodDefinition &&
                                     x.GetGenericArguments().Length == 2)
                        .MakeGenericMethod(executerType, proxyType);
                    var executer = create.Invoke(null, null)!;
                    return executer;
                }

                if (operation == "FirstOrDefaultAsync")
                {
                    var predicate = args?.OfType<Expression<Func<TEntity, bool>>>()
                        .FirstOrDefault();
                    var result = predicate is null
                        ? _rows.FirstOrDefault()
                        : _rows.AsQueryable().FirstOrDefault(predicate);
                    return CompletedTask(method.ReturnType, result);
                }

                if (operation == "GetListAsync")
                {
                    var predicate = args?.OfType<Expression<Func<TEntity, bool>>>()
                        .FirstOrDefault();
                    var result = predicate is null
                        ? _rows.ToList()
                        : _rows.AsQueryable().Where(predicate).ToList();
                    return CompletedTask(method.ReturnType, result);
                }

                if (operation == "GetQueryableAsync")
                {
                    return CompletedTask(method.ReturnType, _rows.AsQueryable());
                }

                throw new InvalidOperationException(
                    $"SECURITY TEST FAILURE: unsupported {typeof(TEntity).Name} repository operation {operation}.");
            }

            private static object CompletedTask(Type returnType, object? value)
            {
                if (!returnType.IsGenericType ||
                    returnType.GetGenericTypeDefinition() != typeof(Task<>))
                {
                    throw new InvalidOperationException(
                        $"Unsupported repository return type: {returnType}.");
                }

                var resultType = returnType.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(resultType)
                    .Invoke(null, new[] { value })!;
            }
        }

        private class AsyncExecuterProxy<TExecutor> : DispatchProxy
        {
            protected override object? Invoke(MethodInfo? method, object?[]? args)
            {
                if (method is null)
                {
                    throw new InvalidOperationException("Async executor method was null.");
                }

                var queryable = args?.OfType<IQueryable>().FirstOrDefault();
                var expression = args?.OfType<LambdaExpression>().FirstOrDefault();
                object? value = queryable;

                if (queryable is not null && expression is not null)
                {
                    var where = typeof(Queryable).GetMethods()
                        .Single(x => x.Name == nameof(Queryable.Where) &&
                                     x.GetParameters().Length == 2 &&
                                     x.GetParameters()[1].ParameterType.IsGenericType &&
                                     x.GetParameters()[1].ParameterType.GetGenericTypeDefinition() ==
                                         typeof(Expression<>) &&
                                     x.GetParameters()[1].ParameterType
                                         .GetGenericArguments()[0].GetGenericArguments().Length == 2)
                        .MakeGenericMethod(queryable.ElementType);
                    value = where.Invoke(null, new object[] { queryable, expression });
                }

                if (method.Name.Contains("FirstOrDefault", StringComparison.Ordinal))
                {
                    value = value is IQueryable filtered
                        ? filtered.Cast<object>().FirstOrDefault()
                        : null;
                }
                else if (method.Name.Contains("ToList", StringComparison.Ordinal) ||
                         method.Name.Contains("GetList", StringComparison.Ordinal))
                {
                    value = value is IQueryable filtered
                        ? filtered.Cast<object>().ToList()
                        : new List<object>();
                }

                return CompletedTask(method.ReturnType, value);
            }

            private static object CompletedTask(Type returnType, object? value)
            {
                if (!returnType.IsGenericType ||
                    returnType.GetGenericTypeDefinition() != typeof(Task<>))
                {
                    throw new InvalidOperationException(
                        $"Unsupported async executor return type: {returnType}.");
                }

                var resultType = returnType.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(resultType)
                    .Invoke(null, new[] { value })!;
            }
        }
    }

}