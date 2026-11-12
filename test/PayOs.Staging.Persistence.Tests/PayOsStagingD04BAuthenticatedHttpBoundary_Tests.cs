using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NgocTrangHouseManagementSystem.Payments;
using Volo.Abp.Authorization;
using Volo.Abp.Security.Claims;
using Xunit;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

/// <summary>
/// Real ASP.NET routing, authentication middleware, distinct encrypted
/// test-cookie sessions and D04-B controller actions in an in-process
/// TestServer. Deliberately FAKE downstream services/Identity: this verifies
/// the HTTP security boundary, NOT native ABP password/OIDC login, any
/// PostgreSQL financial writer, live bank proof, or D04 business acceptance.
///
/// Unlike D04RealLegacyWriterAcceptance_Tests this does NOT require Docker.
/// The complementary writer test checks the three disposable PostgreSQL
/// databases, while a true ABP-login + DB HTTP integration gate remains HOLD.
/// </summary>
public sealed class PayOsStagingD04BAuthenticatedHttpBoundary_Tests
{
    private static readonly Guid ReviewerId = Guid.Parse("b0000000-0000-4000-8000-000000000001");
    private static readonly Guid OwnerId = Guid.Parse("b0000000-0000-4000-8000-000000000002");
    private static readonly Guid OtherId = Guid.Parse("b0000000-0000-4000-8000-000000000003");
    private static readonly Guid IntentId = Guid.Parse("b0000000-0000-4000-8000-000000000004");
    private static readonly Guid ReviewId = Guid.Parse("b0000000-0000-4000-8000-000000000005");
    private static readonly Guid ApprovalId = Guid.Parse("b0000000-0000-4000-8000-000000000006");
    private static readonly Guid ReceiptId = Guid.Parse("b0000000-0000-4000-8000-000000000007");
    private static readonly Guid SettlementId = Guid.Parse("b0000000-0000-4000-8000-000000000008");
    private static readonly Guid OutboxId = Guid.Parse("b0000000-0000-4000-8000-000000000009");

    [Fact]
    public async Task D04B_HTTP01_Distinct_authenticated_sessions_reach_only_their_allowed_routes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Staging
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BuildingManagement:PayOsD04B:Enabled"] = "true",
            ["BuildingManagement:PayOsD04B:Activation"] =
                "I_ACCEPT_DISPOSABLE_D04B_DUAL_AUTH_ONLY",
            ["BuildingManagement:PayOsStaging:Enabled"] = "false",
            ["ConnectionStrings:Default"] = DisposableConnection("payos_d04_identity_test"),
            ["ConnectionStrings:BuildingManagement"] = DisposableConnection("payos_d04_legacy_test"),
            ["ConnectionStrings:PayOsD04BStaging"] = DisposableConnection("payos_ef_migration_test")
        });
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "D04B_Only_InMemory_Test_Cookie";
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
        builder.Services.AddAuthorization();
        // Register ONLY the guarded D04 controller; unrelated Host controllers
        // are not activated in this isolated middleware acceptance fixture.
        // This test assembly is the ASP.NET entry assembly. The actual D04
        // controller is compiled in HttpApi.Host; without an explicit
        // ApplicationPart MVC discovers zero D04 routes and every request
        // returns 404 before [Authorize] can challenge an anonymous caller.
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(PayOsD04StagingReviewController).Assembly)
            .ConfigureApplicationPartManager(parts =>
        {
            foreach (var feature in parts.FeatureProviders
                .OfType<ControllerFeatureProvider>().ToArray())
                parts.FeatureProviders.Remove(feature);
            parts.FeatureProviders.Add(new D04OnlyControllerFeatureProvider());
        });
        builder.Services.AddSingleton<FakeD04Downstream>();
        builder.Services.AddSingleton<IPayOsD04ManualEvidenceService>(
            sp => sp.GetRequiredService<FakeD04Downstream>());
        builder.Services.AddSingleton<IPayOsD04ReviewedPostingCoordinator>(
            sp => sp.GetRequiredService<FakeD04Downstream>());

        await using var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();

        // Synthetic test-only identity issuer. A signed ASP.NET cookie is
        // required for every controller request; no caller may set an actor
        // ID header. This is NOT ABP login and must never be used in Host.
        app.MapPost("/__d04_test_only_sign_in/{name}", async (
            HttpContext context, string name) =>
        {
            var id = name switch
            {
                "reviewer" => ReviewerId,
                "owner" => OwnerId,
                "other" => OtherId,
                _ => Guid.Empty
            };
            if (id == Guid.Empty) return Results.Unauthorized();
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(AbpClaimTypes.UserId, id.ToString("D")),
                new Claim(ClaimTypes.NameIdentifier, id.ToString("D")),
                new Claim(ClaimTypes.Name, "Synthetic D04 Test User")
            }, CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity));
            return Results.NoContent();
        });

        await app.StartAsync();
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost");

        var route = "/api/payments/payos/d04/staging";
        var reviewerCookie = await LoginAsync(client, "reviewer");
        var ownerCookie = await LoginAsync(client, "owner");
        var otherCookie = await LoginAsync(client, "other");
        Assert.NotEqual(reviewerCookie, ownerCookie);
        Assert.NotEqual(ownerCookie, otherCookie);

        // ASP.NET [Authorize], not a direct call to a controller method.
        using (var anonymous = await PostAsync(client,
            $"{route}/approve/{ReviewId:D}", null))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // A caller-controlled Bearer string is NOT a native OpenIddict login.
        // This fixture registers only signed test-cookie authentication, so
        // forged bearer/actor headers must never activate financial routes.
        using (var forgedBearer = new HttpRequestMessage(HttpMethod.Post,
            $"{route}/post/{IntentId:D}"))
        {
            forgedBearer.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", "forged-test-token");
            forgedBearer.Headers.TryAddWithoutValidation("X-Actor-Id", OwnerId.ToString("D"));
            forgedBearer.Headers.TryAddWithoutValidation("X-Actor-Role", "Owner");
            using var rejected = await client.SendAsync(forgedBearer);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }

        using (var ownerCannotReview = await PostAsync(client,
            $"{route}/review/{IntentId:D}", ownerCookie, CreateReviewForm()))
            Assert.Equal(HttpStatusCode.Forbidden, ownerCannotReview.StatusCode);

        using (var reviewed = await PostAsync(client,
            $"{route}/review/{IntentId:D}", reviewerCookie, CreateReviewForm()))
        {
            Assert.Equal(HttpStatusCode.Accepted, reviewed.StatusCode);
            Assert.Equal(ReviewId, await IdAsync(reviewed, "reviewId"));
        }

        using (var selfApproval = await PostAsync(client,
            $"{route}/approve/{ReviewId:D}", reviewerCookie))
            Assert.Equal(HttpStatusCode.Forbidden, selfApproval.StatusCode);

        using (var ownerApproval = await PostAsync(client,
            $"{route}/approve/{ReviewId:D}", ownerCookie))
        {
            Assert.Equal(HttpStatusCode.Accepted, ownerApproval.StatusCode);
            Assert.Equal(ApprovalId, await IdAsync(ownerApproval, "approvalId"));
        }

        using (var unauthorizedStaffPost = await PostAsync(client,
            $"{route}/post/{IntentId:D}", reviewerCookie))
            Assert.Equal(HttpStatusCode.Forbidden, unauthorizedStaffPost.StatusCode);

        using (var unrelatedPost = await PostAsync(client,
            $"{route}/post/{IntentId:D}", otherCookie))
            Assert.Equal(HttpStatusCode.Forbidden, unrelatedPost.StatusCode);

        for (var attempt = 0; attempt != 2; attempt++)
        {
            using var approvedPost = await PostAsync(client,
                $"{route}/post/{IntentId:D}", ownerCookie);
            Assert.Equal(HttpStatusCode.OK, approvedPost.StatusCode);
            Assert.Equal(ReceiptId, await IdAsync(approvedPost, "receiptId"));
        }

        var downstream = app.Services.GetRequiredService<FakeD04Downstream>();
        Assert.Equal(1, downstream.Reviews);
        Assert.Equal(1, downstream.Approvals);
        Assert.Equal(2, downstream.PostRequests);

        // The HTTP controller must fail closed even after valid login when
        // runtime configuration is redirected to a non-disposable database.
        app.Configuration["ConnectionStrings:BuildingManagement"] =
            DisposableConnection("NgocTrangDb");
        using (var refused = await PostAsync(client,
            $"{route}/post/{IntentId:D}", ownerCookie))
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(2, downstream.PostRequests);

        // No financial action may remain reachable after the runtime
        // activation is revoked, even with previously signed test sessions.
        // Check all three verbs, not only posting: review and approval also
        // mutate protected financial evidence/authorization state.
        app.Configuration["ConnectionStrings:BuildingManagement"] =
            DisposableConnection("payos_d04_legacy_test");
        app.Configuration["BuildingManagement:PayOsD04B:Enabled"] = "false";
        using (var deniedReview = await PostAsync(client,
            $"{route}/review/{IntentId:D}", reviewerCookie, CreateReviewForm()))
            Assert.Equal(HttpStatusCode.NotFound, deniedReview.StatusCode);
        using (var deniedApproval = await PostAsync(client,
            $"{route}/approve/{ReviewId:D}", ownerCookie))
            Assert.Equal(HttpStatusCode.NotFound, deniedApproval.StatusCode);
        using (var deniedPost = await PostAsync(client,
            $"{route}/post/{IntentId:D}", ownerCookie))
            Assert.Equal(HttpStatusCode.NotFound, deniedPost.StatusCode);
        Assert.Equal(1, downstream.Reviews);
        Assert.Equal(1, downstream.Approvals);
        Assert.Equal(2, downstream.PostRequests);
    }

    private static string DisposableConnection(string database) =>
        $"Host=127.0.0.1;Port=15432;Database={database};Username=postgres;Password=disposable-only";

    private static async Task<string> LoginAsync(HttpClient client, string name)
    {
        using var login = await client.PostAsync(
            $"/__d04_test_only_sign_in/{name}", null);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var setCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        var cookie = setCookie.Split(';', 2)[0];
        Assert.StartsWith("D04B_Only_InMemory_Test_Cookie=", cookie);
        return cookie;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string url, string? cookie, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Content = content;
        return await client.SendAsync(request);
    }

    private static MultipartFormDataContent CreateReviewForm()
    {
        var bytes = new byte[512];
        Encoding.ASCII.GetBytes("%PDF-1.7\n").CopyTo(bytes, 0);
        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("SYNTH-RECEIPT-HTTP-01"), "BankReference");
        multipart.Add(new StringContent("150000"), "AmountVnd");
        multipart.Add(new StringContent("SYNTHETIC-PRIVATE-ARCHIVE-ONLY"),
            "ProtectedArchiveReference");
        multipart.Add(new StringContent("true"), "ReviewerAttestsBankStatementMatched");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        multipart.Add(file, "Document", "only-synthetic.pdf");
        return multipart;
    }

    private static async Task<Guid> IdAsync(HttpResponseMessage response, string key)
    {
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty(key).GetGuid();
    }

    private sealed class D04OnlyControllerFeatureProvider : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            typeInfo.AsType() == typeof(PayOsD04StagingReviewController);
    }

    private sealed class FakeD04Downstream :
        IPayOsD04ManualEvidenceService, IPayOsD04ReviewedPostingCoordinator
    {
        private readonly IHttpContextAccessor _context;
        public FakeD04Downstream(IHttpContextAccessor context) => _context = context;
        public int Reviews { get; private set; }
        public int Approvals { get; private set; }
        public int PostRequests { get; private set; }

        private Guid Actor()
        {
            var principal = _context.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true ||
                !Guid.TryParse(principal.FindFirst(AbpClaimTypes.UserId)?.Value,
                    out var actor))
                throw new AbpAuthorizationException("No signed-in D04 test principal.");
            return actor;
        }

        public Task<Guid> RecordIndependentBankReviewAsync(
            Guid intentId, PayOsD04ManualBankStatementSubmission evidence,
            CancellationToken ct = default)
        {
            if (Actor() != ReviewerId || intentId != IntentId ||
                evidence.AmountVnd != 150000 ||
                evidence.StatementDocumentBytes.Length != 512 ||
                evidence.BankReference != "SYNTH-RECEIPT-HTTP-01" ||
                !evidence.ReviewerAttestsBankStatementMatched)
                throw new AbpAuthorizationException("Synthetic reviewer only.");
            Reviews++;
            return Task.FromResult(ReviewId);
        }

        public Task<Guid> ApproveReviewedDepositAsync(
            Guid bankReviewId, CancellationToken ct = default)
        {
            if (Actor() != OwnerId || bankReviewId != ReviewId ||
                Reviews != 1)
                throw new AbpAuthorizationException("Synthetic Owner only.");
            Approvals++;
            return Task.FromResult(ApprovalId);
        }

        public Task<PayOsD04PostingOutcome> PostApprovedAsync(
            Guid intentId, CancellationToken ct = default)
        {
            if (Actor() != OwnerId || intentId != IntentId || Approvals != 1)
                throw new AbpAuthorizationException("Synthetic approved Owner only.");
            PostRequests++;
            return Task.FromResult(new PayOsD04PostingOutcome(
                ReceiptId, SettlementId, OutboxId, AckPending: false));
        }
    }
}
