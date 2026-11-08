using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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
using BuildingManagement.Contracts;
using BuildingManagement.DepositSettlements;
using BuildingManagement.EntityFrameworkCore;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using BuildingManagement.Payments;
using BuildingManagement.Payments.BuildingManagement.Payments;
using BuildingManagement.Rooms;
using BuildingManagement.Tenants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using NgocTrangHouseManagementSystem.Payments;
using Volo.Abp.Authorization;
using Volo.Abp.Security.Claims;
using Volo.Abp.Users;
using Xunit;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

/// <summary>
/// D04-B cross-boundary acceptance: ASP.NET HTTP -> guarded real controller
/// -> actual independent reviewer and Owner financial authority -> canonical
/// BuildingManagement PostgreSQL Payment/settlement/journal/outbox ->
/// separate, migrated payOS PostgreSQL Posted ACK.
///
/// Sessions use SIGNED ASP.NET TEST COOKIES (not native ABP/OpenIddict login).
/// The current user is derived exclusively from the authenticated cookie,
/// not from a user-supplied body/header. The real SQL authority checks live
/// Identity-shaped rows and actual Building grants. This is still NOT real
/// ABP-login UAT, independent bank verification, or production acceptance.
///
/// Called ONLY by D04RealLegacyWriterAcceptance_Tests after the disposable
/// three-database safety opt-in and canonical Building migration; no normal
/// unit test and no production DI route can invoke this helper.
/// </summary>
internal static class D04BHttpToRealPostgreSqlGateway
{
    public static async Task VerifyAsync(
        BuildingManagementDbContext db,
        PayOsPaymentMigrationDbContext staging,
        Guid buildingId, Guid tenantId, Guid reviewerId, Guid ownerId,
        PayOsD04StagingDualAuthority authority)
    {
        var floor = await db.Floors.AsNoTracking()
            .SingleAsync(x => x.BuildingId == buildingId);
        var roomId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var intentId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        const long amount = 175000;
        var bankReference = "D04B-HTTP-SYNTH-" + Guid.NewGuid().ToString("N");

        // THIRD ContractDeposit, entirely independent of both earlier flows.
        db.Rooms.Add(new Room(roomId, floor.Id, "H" +
            Guid.NewGuid().ToString("N")[..8], "HTTP-only disposable", 20m, 2,
            RoomStatus.Occupied));
        var contract = new Contract(contractId,
            "D04B-HTTP-CONTRACT-" + Guid.NewGuid().ToString("N"),
            roomId, now.Date, null, amount);
        contract.Activate();
        db.Contracts.Add(contract);
        db.ContractTenants.Add(new ContractTenant(
            Guid.NewGuid(), contractId, tenantId,
            ContractTenantRole.PrimaryTenant));
        db.Payments.Add(new Payment(paymentId,
            "D04B-HTTP-PAY-" + Guid.NewGuid().ToString("N"),
            tenantId, PaymentPurpose.ContractDeposit,
            PaymentMethod.BankTransfer, PaymentChannel.VietQr,
            amount, contractId: contractId));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var intent = new PayOsPaymentIntent(
            intentId, buildingId, tenantId,
            System.Security.Cryptography.RandomNumberGenerator.GetInt32(
                1, int.MaxValue), amount, now.AddDays(2),
            paymentId: paymentId);
        intent.AttachProviderLink("D04B-HTTP-SYNTH-LINK-" + intentId.ToString("N"));
        intent.MarkConfirmed(bankReference, now.AddMinutes(-5));
        var inbox = new PayOsWebhookInboxRecord(
            Guid.NewGuid(), buildingId, intentId, intent.OrderCode,
            bankReference, new string('e', 64), now.AddMinutes(-4));
        inbox.MarkReconciled(now.AddMinutes(-3));
        staging.PayOsPaymentIntents.Add(intent);
        staging.PayOsWebhookInbox.Add(inbox);
        await staging.SaveChangesAsync();
        staging.ChangeTracker.Clear();

        // Dedicated synthetic-only encrypted archive. No bank documents,
        // externally reachable listener or production key/storage used.
        var vaultDir = Path.Combine(Path.GetTempPath(),
            "payos-d04-http-vault-" + Guid.NewGuid().ToString("N"));
        using var vaultCleanup = new D04BDisposableVaultCleanup(vaultDir);
        var vaultKey = RandomNumberGenerator.GetBytes(32);
        using var vault = new D04BDisposableProtectedEvidenceVault(vaultDir, vaultKey);
        CryptographicOperations.ZeroMemory(vaultKey);

        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = Environments.Staging });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["BuildingManagement:PayOsD04B:Enabled"] = "true",
                ["BuildingManagement:PayOsD04B:Activation"] =
                    "I_ACCEPT_DISPOSABLE_D04B_DUAL_AUTH_ONLY",
                ["BuildingManagement:PayOsStaging:Enabled"] = "false",
                ["ConnectionStrings:Default"] =
                    D04RealLegacyWriterAcceptance_TestsIdentityConnection,
                ["ConnectionStrings:BuildingManagement"] =
                    D04RealLegacyWriterAcceptance_Tests.Connection,
                ["ConnectionStrings:PayOsD04BStaging"] =
                    "Host=127.0.0.1;Port=15432;Database=payos_ef_migration_test;" +
                    "Username=postgres;Password=disposable-only;Pooling=false"
            });
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "D04B_RealSql_Test_Cookie";
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
        // The TestHost entry assembly is the test DLL, not HttpApi.Host.
        // Explicit ApplicationPart is mandatory; otherwise 404 can mask
        // missing [Authorize] challenges.
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(PayOsD04StagingReviewController).Assembly)
            .ConfigureApplicationPartManager(parts =>
            {
                foreach (var provider in parts.FeatureProviders
                    .OfType<ControllerFeatureProvider>().ToArray())
                    parts.FeatureProviders.Remove(provider);
                parts.FeatureProviders.Add(new D04RealSqlOnlyControllerProvider());
            });
        builder.Services.AddSingleton<IPayOsD04ProtectedBankEvidenceArchive>(vault);
        builder.Services.AddSingleton<SqlBackedD04HttpAdapter>(sp =>
            new SqlBackedD04HttpAdapter(
                db, staging, authority,
                sp.GetRequiredService<IHttpContextAccessor>(),
                sp.GetRequiredService<IPayOsD04ProtectedBankEvidenceArchive>()));
        builder.Services.AddSingleton<IPayOsD04ManualEvidenceService>(
            sp => sp.GetRequiredService<SqlBackedD04HttpAdapter>());
        builder.Services.AddSingleton<IPayOsD04ReviewedPostingCoordinator>(
            sp => sp.GetRequiredService<SqlBackedD04HttpAdapter>());

        await using var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        // Test-server-only cookie issuer. Never registered in production
        // Host and no external socket is opened by UseTestServer.
        app.MapPost("/__d04b_sql_test_login/{name}", async (
            HttpContext context, string name) =>
        {
            var id = name switch
            {
                "reviewer" => reviewerId,
                "owner" => ownerId,
                "unknown" => Guid.NewGuid(),
                _ => Guid.Empty
            };
            if (id == Guid.Empty) return Results.Unauthorized();
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(AbpClaimTypes.UserId, id.ToString("D")),
                new Claim(ClaimTypes.NameIdentifier, id.ToString("D"))
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
        var unknownCookie = await LoginAsync(client, "unknown");
        Assert.NotEqual(reviewerCookie, ownerCookie);
        Assert.NotEqual(ownerCookie, unknownCookie);

        // No anonymous review or money posting; ASP.NET auth middleware
        // decides these responses BEFORE the financial services execute.
        using (var anonymous = await PostAsync(
            client, $"{route}/post/{intentId:D}", null))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using (var unknownReview = await PostAsync(client,
            $"{route}/review/{intentId:D}", unknownCookie,
            StatementForm(bankReference, amount)))
            Assert.Equal(HttpStatusCode.Forbidden, unknownReview.StatusCode);

        using (var ownerReview = await PostAsync(client,
            $"{route}/review/{intentId:D}", ownerCookie,
            StatementForm(bankReference, amount)))
            Assert.Equal(HttpStatusCode.Forbidden, ownerReview.StatusCode);

        using (var ownerPrematurePost = await PostAsync(client,
            $"{route}/post/{intentId:D}", ownerCookie))
            Assert.Equal(HttpStatusCode.Forbidden, ownerPrematurePost.StatusCode);

        Guid bankReviewId;
        using (var review = await PostAsync(client,
            $"{route}/review/{intentId:D}", reviewerCookie,
            StatementForm(bankReference, amount)))
        {
            Assert.Equal(HttpStatusCode.Accepted, review.StatusCode);
            bankReviewId = await ResponseIdAsync(review, "reviewId");
            Assert.NotEqual(Guid.Empty, bankReviewId);
        }

        // The real SQL bank review MUST store the SERVER-ISSUED protected
        // reference, not the untrusted multipart form value.
        await using (var proofConn = new Npgsql.NpgsqlConnection(
            D04RealLegacyWriterAcceptance_Tests.Connection))
        {
            await proofConn.OpenAsync();
            await using var statement = new Npgsql.NpgsqlCommand("""
                SELECT "ExternalBankProofReference","DocumentSha256"
                  FROM "BuildingManagementPayOsD04BankReviews"
                 WHERE "Id"=@id
                """, proofConn);
            statement.Parameters.AddWithValue("id", bankReviewId);
            await using var reader = await statement.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var archiveReference = reader.GetString(0);
            var digest = reader.GetString(1);
            Assert.StartsWith("d04vault1:", archiveReference);
            Assert.NotEqual("../CLIENT-UNTRUSTED-IGNORE",
                archiveReference);
            Assert.True(await vault.VerifyAsync(
                intentId, reviewerId, archiveReference, digest));
        }

        // The same reviewer cannot approve their own bank attestation.
        using (var denied = await PostAsync(client,
            $"{route}/approve/{bankReviewId:D}", reviewerCookie))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using (var approved = await PostAsync(client,
            $"{route}/approve/{bankReviewId:D}", ownerCookie))
        {
            Assert.Equal(HttpStatusCode.Accepted, approved.StatusCode);
            Assert.NotEqual(Guid.Empty,
                await ResponseIdAsync(approved, "approvalId"));
        }

        using (var wrongPost = await PostAsync(client,
            $"{route}/post/{intentId:D}", reviewerCookie))
            Assert.Equal(HttpStatusCode.Forbidden, wrongPost.StatusCode);

        // This call crosses ALL THREE actual PostgreSQL databases.
        // Unlike D04B_HTTP01's mocked downstream, the real writer updates
        // Payment and settlement and creates an immutable journal/outbox.
        Guid firstReceiptId;
        using (var posted = await PostAsync(client,
            $"{route}/post/{intentId:D}", ownerCookie))
        {
            Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
            firstReceiptId = await ResponseIdAsync(posted, "receiptId");
            Assert.NotEqual(Guid.Empty, firstReceiptId);
        }

        db.ChangeTracker.Clear();
        staging.ChangeTracker.Clear();
        var payment = await db.Payments.AsNoTracking()
            .SingleAsync(x => x.Id == paymentId);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(amount, payment.Amount);
        Assert.Equal(bankReference, payment.TransactionReference);
        var settlement = await db.DepositSettlements.AsNoTracking()
            .SingleAsync(x => x.PaymentId == paymentId);
        Assert.Equal(DepositSettlementStatus.Completed, settlement.Status);
        Assert.Equal(amount, settlement.Amount);

        var proof = await new PayOsD04CommittedReceiptStore(db)
            .ReadCommittedAsync(intentId, CancellationToken.None);
        Assert.NotNull(proof);
        Assert.Equal(firstReceiptId, proof.Proof.ReceiptId);
        Assert.True(proof.OutboxAcknowledged);
        Assert.Equal(PayOsPaymentIntentStatus.Posted,
            (await staging.PayOsPaymentIntents.AsNoTracking()
                .SingleAsync(x => x.Id == intentId)).Status);

        using (var replay = await PostAsync(client,
            $"{route}/post/{intentId:D}", ownerCookie))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(firstReceiptId,
                await ResponseIdAsync(replay, "receiptId"));
        }

        db.ChangeTracker.Clear();
        staging.ChangeTracker.Clear();
        Assert.Single(await db.DepositSettlements.AsNoTracking()
            .Where(x => x.PaymentId == paymentId).ToListAsync());
        var afterReplay = await new PayOsD04CommittedReceiptStore(db)
            .ReadCommittedAsync(intentId, CancellationToken.None);
        Assert.NotNull(afterReplay);
        Assert.Equal(firstReceiptId, afterReplay.Proof.ReceiptId);
        Assert.True(afterReplay.OutboxAcknowledged);
    }

    private const string D04RealLegacyWriterAcceptance_TestsIdentityConnection =
        "Host=127.0.0.1;Port=15432;Database=payos_d04_identity_test;" +
        "Username=postgres;Password=disposable-only;Pooling=false;Timeout=5";

    private static async Task<string> LoginAsync(HttpClient client, string user)
    {
        using var login = await client.PostAsync(
            $"/__d04b_sql_test_login/{user}", null);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"))
            .Split(';', 2)[0];
        Assert.StartsWith("D04B_RealSql_Test_Cookie=", cookie);
        return cookie;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string url, string? cookie, HttpContent? data = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = data
        };
        if (cookie is not null)
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return await client.SendAsync(request);
    }

    private static MultipartFormDataContent StatementForm(
        string bankReference, long amount)
    {
        var document = new byte[512];
        Encoding.ASCII.GetBytes("%PDF-1.7\n").CopyTo(document, 0);
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(bankReference), "BankReference");
        content.Add(new StringContent(amount.ToString(
            System.Globalization.CultureInfo.InvariantCulture)), "AmountVnd");
        // This deliberately untrusted, path-like field MUST be ignored.
        content.Add(new StringContent("../CLIENT-UNTRUSTED-IGNORE"),
            "ProtectedArchiveReference");
        content.Add(new StringContent("true"),
            "ReviewerAttestsBankStatementMatched");
        var file = new ByteArrayContent(document);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "Document", "synthetic-only.pdf");
        return content;
    }

    private static async Task<Guid> ResponseIdAsync(
        HttpResponseMessage response, string field)
    {
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty(field).GetGuid();
    }

    private sealed class D04BDisposableVaultCleanup : IDisposable
    {
        private readonly string _path;
        public D04BDisposableVaultCleanup(string path) => _path = path;
        public void Dispose()
        {
            // Only our uniquely named, locally created synthetic scratch
            // directory. The test never touches user or production files.
            if (Directory.Exists(_path))
                Directory.Delete(_path, recursive: true);
        }
    }

    private sealed class D04RealSqlOnlyControllerProvider : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            typeInfo.AsType() == typeof(PayOsD04StagingReviewController);
    }

    // Only the identity extraction is test-provided (signed, distinct
    // ASP.NET cookies). Everything after it is the actual D04-B SQL-backed
    // application service, commit-bound authority, writer and ACK.
    private sealed class SqlBackedD04HttpAdapter :
        IPayOsD04ManualEvidenceService,
        IPayOsD04ReviewedPostingCoordinator
    {
        private readonly BuildingManagementDbContext _db;
        private readonly PayOsPaymentMigrationDbContext _staging;
        private readonly PayOsD04StagingDualAuthority _authority;
        private readonly IHttpContextAccessor _context;
        private readonly IPayOsD04ProtectedBankEvidenceArchive _archive;

        public SqlBackedD04HttpAdapter(
            BuildingManagementDbContext db,
            PayOsPaymentMigrationDbContext staging,
            PayOsD04StagingDualAuthority authority,
            IHttpContextAccessor context,
            IPayOsD04ProtectedBankEvidenceArchive archive)
        {
            _db = db;
            _staging = staging;
            _authority = authority;
            _context = context;
            _archive = archive;
        }

        private ICurrentUser CurrentActor()
        {
            var principal = _context.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true ||
                !Guid.TryParse(
                    principal.FindFirst(AbpClaimTypes.UserId)?.Value, out var id) ||
                id == Guid.Empty)
                throw new AbpAuthorizationException(
                    "An authenticated D04-B test cookie is required.");
            var current = new Mock<ICurrentUser>();
            current.SetupGet(u => u.Id).Returns(id);
            current.SetupGet(u => u.IsAuthenticated).Returns(true);
            return current.Object;
        }

        private PayOsD04TrustedStagingEvidenceReader StagingReader() =>
            new(_staging);

        // Every request is independently bound to the current signed
        // test session; no cross-request cached reviewer/Owner identity.
        private IPayOsD04ManualEvidenceService ArchiveBackedActor()
        {
            var actor = CurrentActor();
            var inner = new PayOsD04ManualEvidenceService(
                _db, StagingReader(), _authority, actor);
            return new PayOsD04ArchiveBackedManualEvidenceService(
                inner, _archive, _db, actor);
        }

        public Task<Guid> RecordIndependentBankReviewAsync(
            Guid intentId, PayOsD04ManualBankStatementSubmission evidence,
            CancellationToken ct = default) =>
            ArchiveBackedActor().RecordIndependentBankReviewAsync(
                intentId, evidence, ct);

        public Task<Guid> ApproveReviewedDepositAsync(
            Guid bankReviewId, CancellationToken ct = default) =>
            ArchiveBackedActor().ApproveReviewedDepositAsync(bankReviewId, ct);

        public Task<PayOsD04PostingOutcome> PostApprovedAsync(
            Guid intentId, CancellationToken ct = default)
        {
            var reader = StagingReader();
            var store = new PayOsD04CommittedReceiptStore(_db);
            var writer = new PayOsD04LegacyPostingWriter(
                _db, reader, _authority);
            var ack = new PayOsD04StagingReceiptAcknowledger(_staging, store);
            return new PayOsD04ReviewedPostingCoordinator(
                _db, writer, ack, CurrentActor())
                .PostApprovedAsync(intentId, ct);
        }
    }
}
