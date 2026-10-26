using System;
using System.Linq;
using System.Net;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using BuildingManagement;
using BuildingManagement.Samples;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace NgocTrangHouseManagementSystem.Security.Tests;

/// <summary>
/// A disposable in-memory ASP.NET Core pipeline, NOT the real ABP host.
/// It discovers the original BuildingManagement.HttpApi controller assembly,
/// applies the actual security convention and verifies an anonymous request
/// gets challenged before any application-service dependency is resolved.
/// No DB connection or business mutation is configured.
/// </summary>
public sealed class BuildingManagementExplicitHttpAuthorization_Tests
{
    [Fact]
    public void APIHTTP01_Explicit_Routes_Are_Discovered_With_Auth_Filter()
    {
        using var server = CreateIsolatedServer();

        var actions = server.Services
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(x => x.ControllerTypeInfo.Assembly ==
                        typeof(BuildingManagementHttpApiModule).Assembly)
            .ToList();

        Assert.NotEmpty(actions);

        var sample = Assert.Single(actions.Where(x =>
            x.ControllerTypeInfo.AsType() == typeof(ExampleController) &&
            x.MethodInfo.Name == nameof(ExampleController.GetAsync)));
        Assert.Contains("building-management/example",
            sample.AttributeRouteInfo?.Template ?? string.Empty);

        foreach (var action in actions)
        {
            var authFilters = action.FilterDescriptors
                .Select(x => x.Filter)
                .OfType<AuthorizeFilter>()
                .ToList();

            Assert.Contains(authFilters, filter =>
                filter.Policy is not null &&
                filter.Policy.Requirements.Any(x =>
                    x is DenyAnonymousAuthorizationRequirement));
        }
    }

    [Fact]
    public async Task APIHTTP02_Anonymous_Explicit_Get_Is_Challenged_With_401()
    {
        using var server = CreateIsolatedServer();
        using var client = server.CreateClient();

        using var response = await client.GetAsync(
            "/api/building-management/example");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static TestServer CreateIsolatedServer()
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication("ApiAuthProbe")
                    .AddScheme<AuthenticationSchemeOptions,
                        RejectAnonymousAuthenticationHandler>(
                        "ApiAuthProbe", _ => { });
                services.AddAuthorization();
                services.AddControllers(options =>
                    {
                        options.Conventions.Add(
                            new BuildingManagementAuthenticatedControllerConvention());
                    })
                    .AddApplicationPart(typeof(ExampleController).Assembly);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(routes => routes.MapControllers());
            });

        return new TestServer(builder);
    }

    private sealed class RejectAnonymousAuthenticationHandler
        : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public RejectAnonymousAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.NoResult());
    }
}
