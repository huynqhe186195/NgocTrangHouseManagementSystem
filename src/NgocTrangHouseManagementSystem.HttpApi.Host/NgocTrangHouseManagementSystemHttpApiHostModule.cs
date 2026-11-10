using BuildingManagement;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NgocTrangHouseManagementSystem.Security;
using BuildingManagement.Security;
using Npgsql;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using System.Net.Http;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using BuildingManagement.Payments;
using NgocTrangHouseManagementSystem.Payments;
using Microsoft.OpenApi;
using NgocTrangHouseManagementSystem.EntityFrameworkCore;
using NgocTrangHouseManagementSystem.HealthChecks;
using NgocTrangHouseManagementSystem.MultiTenancy;
using NgocTrangHouseManagementSystem.Swagger;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Volo.Abp;
using Volo.Abp.Account;
using Volo.Abp.Account.Web;
using Volo.Abp.AspNetCore.ExceptionHandling;
using Volo.Abp.AspNetCore.MultiTenancy;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.AspNetCore.Mvc.Libs;
using Volo.Abp.AspNetCore.Mvc.UI.Bundling;
using Volo.Abp.AspNetCore.Mvc.UI.Theme.Basic;
using Volo.Abp.AspNetCore.Mvc.UI.Theme.Basic.Bundling;
using Volo.Abp.AspNetCore.Mvc.UI.Theme.Shared;
using Volo.Abp.AspNetCore.Serilog;
using Volo.Abp.Autofac;
using Volo.Abp.Identity;
using Volo.Abp.Localization;
using Volo.Abp.Modularity;
using Volo.Abp.OpenIddict;
using Volo.Abp.Security.Claims;
using Volo.Abp.Studio;
using Volo.Abp.Studio.Client.AspNetCore;
using Volo.Abp.Swashbuckle;
using Volo.Abp.UI.Navigation.Urls;
using Volo.Abp.VirtualFileSystem;

namespace NgocTrangHouseManagementSystem;

[DependsOn(
    typeof(NgocTrangHouseManagementSystemHttpApiModule),
    typeof(AbpStudioClientAspNetCoreModule),
    typeof(AbpAspNetCoreMvcUiBasicThemeModule),
    typeof(AbpAutofacModule),
    typeof(AbpAspNetCoreMultiTenancyModule),
    typeof(NgocTrangHouseManagementSystemApplicationModule),
    typeof(NgocTrangHouseManagementSystemEntityFrameworkCoreModule),
    typeof(AbpAccountWebOpenIddictModule),
    typeof(AbpSwashbuckleModule),
    typeof(AbpAspNetCoreSerilogModule),
    typeof(BuildingManagementApplicationModule),
    typeof(BuildingManagementHttpApiModule)
    )]
public class NgocTrangHouseManagementSystemHttpApiHostModule : AbpModule // system tell abp that system must active rules
{
    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        var hostingEnvironment = context.Services.GetHostingEnvironment();
        var configuration = context.Services.GetConfiguration();

        PreConfigure<IMvcBuilder>(mvcBuilder =>
        {
            mvcBuilder.AddApplicationPartIfNotExists(
                typeof(BuildingManagementApplicationModule).Assembly
            );
        });

        PreConfigure<AbpAspNetCoreMvcOptions>(options =>
        {
            options.ConventionalControllers.Create(
                typeof(BuildingManagementApplicationModule).Assembly,
                settings =>
                {
                    settings.RootPath = "building-management";
                }
            );
        });

        PreConfigure<OpenIddictBuilder>(builder =>
        {
            builder.AddValidation(options =>
            {
                options.AddAudiences("NgocTrangHouseManagementSystem");
                options.UseLocalServer();
                options.UseAspNetCore();
            });
        });

        if (!hostingEnvironment.IsDevelopment())
        {
            PreConfigure<AbpOpenIddictAspNetCoreOptions>(options =>
            {
                options.AddDevelopmentEncryptionAndSigningCertificate = false;
            });

            PreConfigure<OpenIddictServerBuilder>(serverBuilder =>
            {
                serverBuilder.AddProductionEncryptionAndSigningCertificate("openiddict.pfx", configuration["AuthServer:CertificatePassPhrase"]!);
                serverBuilder.SetIssuer(new Uri(configuration["AuthServer:Authority"]!));
            });
        }
    }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();
        var hostingEnvironment = context.Services.GetHostingEnvironment();

        // Server-controlled root IDs; default EMPTY, no permission/role seed.
        context.Services.Configure<BuildingManagement.Security.BuildingOwnerBootstrapSecurityOptions>(
            configuration.GetSection("BuildingManagement:Bootstrap"));

        // STAGING OPT-IN ONLY. Both the environment and the isolated PG
        // DATABASE NAMES are checked independently of the activation token.
        // No production instance can enable this with a flag or role.
        if (configuration.GetValue<bool>("BuildingManagement:SliceAStaging:Enabled"))
        {
            if (!hostingEnvironment.IsStaging() ||
                configuration["BuildingManagement:SliceAStaging:Activation"] !=
                    "I_ACCEPT_ISOLATED_SLICE_A_STAGING_WRITES")
                throw new InvalidOperationException(
                    "Slice A cannot be enabled outside reviewed isolated staging.");

            var buildingCs = configuration.GetConnectionString("BuildingManagement");
            var identityCs = configuration.GetConnectionString("Default");
            if (!HaveTwoIsolatedSliceADatabases(buildingCs, identityCs))
                throw new InvalidOperationException(
                    "Slice A requires TWO distinct isolated PostgreSQL Identity and Building databases.");

            var roots = configuration.GetSection(
                "BuildingManagement:Bootstrap:RootUserIds").Get<Guid[]>() ?? Array.Empty<Guid>();
            if (roots.Length != 1 || roots[0] == Guid.Empty)
                throw new InvalidOperationException(
                    "One explicitly designated root account is required for staging bootstrap.");

            context.Services.Replace(ServiceDescriptor.Transient<
                IBuildingOperatorActiveAccountReader>(sp =>
                    new AbpIdentityBuildingOperatorAccountProbe(
                        sp.GetRequiredService<Volo.Abp.Identity.IIdentityUserRepository>(),
                        sp.GetRequiredService<Volo.Abp.Identity.IdentityUserManager>())));
            context.Services.Replace(ServiceDescriptor.Transient<
                IBuildingOperatorGrantReader, SliceAStagingGrantReader>());
            context.Services.Replace(ServiceDescriptor.Transient<
                IBuildingOwnerBootstrapWriter, SliceAStagingFirstOwnerWriter>());
            context.Services.Replace(ServiceDescriptor.Transient<
                IBuildingOperatorGrantMutationWriter, SliceAStagingGrantMutationWriter>());
            context.Services.AddTransient<SliceAStagingIdentityCommitLease>();
            context.Services.AddTransient<SliceAStagingOperatorGrantQuery>();
            context.Services.AddTransient<SliceBStagingContractQuery>();
            context.Services.AddTransient<SliceBStagingReservationQuery>();
            context.Services.AddTransient<SliceBStagingContractMutation>();
            context.Services.AddTransient<SliceBStagingFinalInvoiceMutation>();
            context.Services.AddTransient<SliceBStagingRoomChangeRequests>();
            context.Services.AddTransient<SliceBStagingRoomScopedServiceTariffs>();
            context.Services.AddTransient<SliceCStagingUtilityRates>();
            context.Services.AddTransient<SliceCStagingMeterReadings>();
            context.Services.AddTransient<SliceCStagingUtilityMeters>();
            context.Services.Replace(ServiceDescriptor.Transient<
                IBuildingScopedWriteBoundary, SliceAStagingWriteBoundary>());

            // Recovery is a SECOND, independent opt-in. A co-signer NEVER
            // joins the BootstrapOwner allowlist and cannot create buildings.
            // No remote recovery endpoints are registered.
            if (configuration.GetValue<bool>(
                    "BuildingManagement:OwnerRecovery:Enabled"))
            {
                var signers = configuration.GetSection(
                    "BuildingManagement:OwnerRecovery:CoSignerUserIds")
                    .Get<Guid[]>() ?? Array.Empty<Guid>();
                if (signers.Length != 1 || signers[0] == Guid.Empty ||
                    signers[0] == roots[0])
                    throw new InvalidOperationException(
                        "Owner recovery requires one separate designated co-signer.");
                Configure<BuildingOwnerRecoverySecurityOptions>(options =>
                    options.CoSignerUserIds = new HashSet<Guid>(signers));
                context.Services.AddTransient<SliceAStagingOwnerRecoveryEvidenceStore>();
                context.Services.Replace(ServiceDescriptor.Transient<
                    IBuildingOwnerRecoveryApprovalReader>(sp =>
                        sp.GetRequiredService<SliceAStagingOwnerRecoveryEvidenceStore>()));
                context.Services.Replace(ServiceDescriptor.Transient<
                    IBuildingOwnerRecoveryFounderReader>(sp =>
                        sp.GetRequiredService<SliceAStagingOwnerRecoveryEvidenceStore>()));
                context.Services.Replace(ServiceDescriptor.Transient<
                    IBuildingOwnerRecoveryWriter, SliceAStagingOwnerRecoveryWriter>());
            }
        }

        // Separate payOS staging activation. NEVER enabled by a regular
        // development/production flag or by an unrelated admin permission.
        // A direct provider API call may move real money even in staging;
        // registration therefore requires explicit operator consent and a
        // dedicated loopback-only disposable PostgreSQL database.
        if (configuration.GetValue<bool>("BuildingManagement:PayOsStaging:Enabled"))
        {
            if (!hostingEnvironment.IsStaging() ||
                configuration["BuildingManagement:PayOsStaging:Activation"] !=
                    "I_ACCEPT_ISOLATED_PAYOS_STAGING_ONLY")
                throw new InvalidOperationException(
                    "payOS integration may only activate in explicitly approved isolated staging.");

            var payOsConnection = configuration.GetConnectionString("PayOsStaging");
            if (string.IsNullOrWhiteSpace(payOsConnection))
                throw new InvalidOperationException("Dedicated payOS staging database missing.");

            var payOsDb = new NpgsqlConnectionStringBuilder(payOsConnection);
            var host = payOsDb.Host?.ToLowerInvariant();
            if (host is not ("127.0.0.1" or "localhost") ||
                payOsDb.Database is null ||
                !payOsDb.Database.StartsWith("bm_payos_staging_", StringComparison.OrdinalIgnoreCase) ||
                payOsDb.Database.Length <= "bm_payos_staging_".Length)
                throw new InvalidOperationException(
                    "payOS staging must use an explicitly named loopback-only disposable database.");

            // BuildingManagement must be a separate DB; do not allow an
            // accidental payment migration in its legacy snapshot/drifted DB.
            var buildingConnection = configuration.GetConnectionString("BuildingManagement");
            var identityConnection = configuration.GetConnectionString("Default");
            foreach (var other in new[] { buildingConnection, identityConnection })
            {
                if (string.IsNullOrWhiteSpace(other)) continue;
                var otherDb = new NpgsqlConnectionStringBuilder(other);
                if (string.Equals(otherDb.Database, payOsDb.Database,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "payOS staging database must not be shared with Building or Identity.");
            }

            var clientId = configuration["BuildingManagement:PayOsStaging:ClientId"];
            var apiKey = configuration["BuildingManagement:PayOsStaging:ApiKey"];
            var checksumKey = configuration["BuildingManagement:PayOsStaging:ChecksumKey"];
            if (string.IsNullOrWhiteSpace(clientId) ||
                string.IsNullOrWhiteSpace(apiKey) ||
                string.IsNullOrWhiteSpace(checksumKey))
                throw new InvalidOperationException(
                    "payOS credentials must be provisioned from server-side secrets.");

            var returnUrl = configuration["BuildingManagement:PayOsStaging:ReturnUrl"];
            var cancelUrl = configuration["BuildingManagement:PayOsStaging:CancelUrl"];
            if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var returnUri) ||
                !Uri.TryCreate(cancelUrl, UriKind.Absolute, out var cancelUri) ||
                returnUri.Scheme != Uri.UriSchemeHttps ||
                cancelUri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(returnUri.UserInfo) ||
                !string.IsNullOrEmpty(cancelUri.UserInfo))
                throw new InvalidOperationException(
                    "payOS return/cancel callback URLs must be server-configured HTTPS URLs.");

            context.Services.AddDbContext<PayOsPaymentMigrationDbContext>(
                options => options.UseNpgsql(payOsConnection,
                    db => db.MigrationsHistoryTable("__EFMigrationsHistory_PayOs")));
            context.Services.AddHttpClient("PayOsStaging");
            context.Services.AddSingleton(new PayOsStagingCheckoutOptions(returnUri, cancelUri));
            context.Services.AddScoped<IPayOsPaymentLinkProvider>(services =>
                new PayOsPaymentLinkClient(
                    services.GetRequiredService<IHttpClientFactory>().CreateClient("PayOsStaging"),
                    clientId, apiKey, checksumKey));
            context.Services.AddScoped<PayOsStagingCheckoutService>();
            context.Services.AddScoped<PayOsStagingReconciliationService>();
            context.Services.AddScoped<PayOsStagingWebhookInboxWriter>(services =>
                new PayOsStagingWebhookInboxWriter(
                    services.GetRequiredService<PayOsPaymentMigrationDbContext>(),
                    checksumKey));
            // No migration, provider call or webhook registration is executed
            // at startup. All requests still require endpoint-level gates.
        }

        // D04-B is a separate, default-OFF TEST-ONLY three-database gate.
        // It cannot enable the live merchant checkout, production Identity or
        // Building DBs, and grants no permission/role automatically.
        // This is the only way review/Owner/post HTTP services are registered.
        // The checks live in an internal helper so the fail-closed gate stays
        // directly regression-testable (D04B_GATE) without booting the Host.
        ConfigurePayOsD04BIfEnabled(configuration, hostingEnvironment, context.Services);

        Configure<AbpMvcLibsOptions>(options =>
        {
            options.CheckLibs = false;
        });

        // Covers only controllers generated from the BuildingManagement
        // application assembly. It does NOT replace operation permissions or
        // server-side building/room scope authorization.
        Configure<MvcOptions>(options =>
        {
            options.Conventions.Add(
                new BuildingManagementAuthenticatedControllerConvention());
        });


        // Never print complete JWTs or personally identifiable security artifacts.
        // This is also required for localhost OAuth acceptance and staging.
        Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = false;
        Microsoft.IdentityModel.Logging.IdentityModelEventSource.LogCompleteSecurityArtifact = false;

        if (!configuration.GetValue<bool>("AuthServer:RequireHttpsMetadata"))
        {
            Configure<OpenIddictServerAspNetCoreOptions>(options =>
            {
                options.DisableTransportSecurityRequirement = true;
            });
            
            Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            });
        }

        if (hostingEnvironment.IsDevelopment())
        {
            context.Services.AddRazorPages()
                .AddRazorRuntimeCompilation();
        }

        ConfigureStudio(hostingEnvironment);
        ConfigureAuthentication(context);
        ConfigureUrls(configuration);
        ConfigureBundles(hostingEnvironment);
        ConfigureConventionalControllers();
        ConfigureHealthChecks(context);
        ConfigureSwagger(context, configuration);
        ConfigureVirtualFileSystem(context);
        ConfigureCors(context, configuration);
        ConfigureErrorHttpStatusCodeMappings();
    }

    /// <summary>
    /// D04-B startup gate, deliberately separate from Host boot so the
    /// fail-closed checks are regression-testable without a real environment,
    /// database or registered ABP module (D04B_GATE). Default OFF.
    /// </summary>
    internal static void ConfigurePayOsD04BIfEnabled(
        IConfiguration configuration,
        IHostEnvironment hostingEnvironment,
        IServiceCollection services)
    {
        if (!configuration.GetValue<bool>("BuildingManagement:PayOsD04B:Enabled"))
            return;

        if (!hostingEnvironment.IsStaging() ||
            configuration["BuildingManagement:PayOsD04B:Activation"] !=
                "I_ACCEPT_DISPOSABLE_D04B_DUAL_AUTH_ONLY" ||
            configuration.GetValue<bool>("BuildingManagement:PayOsStaging:Enabled"))
            throw new InvalidOperationException(
                "D04-B requires dedicated no-merchant, three-DB disposable staging.");

        var identityD04 = configuration.GetConnectionString("Default");
        var legacyD04 = configuration.GetConnectionString("BuildingManagement");
        var stagingD04 = configuration.GetConnectionString("PayOsD04BStaging");
        if (!ExactD04BDisposableDatabase(identityD04, "payos_d04_identity_test") ||
            !ExactD04BDisposableDatabase(legacyD04, "payos_d04_legacy_test") ||
            !ExactD04BDisposableDatabase(stagingD04, "payos_ef_migration_test"))
            throw new InvalidOperationException(
                "D04-B rejects anything but three exact disposable PostgreSQL databases.");

        // Explicit disposable-only encrypted archive for native OpenIddict UAT.
        // The key and absolute storage path are operator-supplied; no defaults,
        // no production registration, no real bank documents permitted.
        if (configuration.GetValue<bool>("BuildingManagement:PayOsD04B:DisposableArchive:Enabled"))
        {
            var archiveRoot = configuration["BuildingManagement:PayOsD04B:DisposableArchive:Root"];
            var archiveKey = configuration["BuildingManagement:PayOsD04B:DisposableArchive:KeyBase64"];
            if (string.IsNullOrWhiteSpace(archiveRoot) ||
                !Path.IsPathFullyQualified(archiveRoot) ||
                string.IsNullOrWhiteSpace(archiveKey))
                throw new InvalidOperationException("D04-B disposable archive requires absolute root and operator key.");
            byte[] key;
            try { key = Convert.FromBase64String(archiveKey); }
            catch (FormatException) { throw new InvalidOperationException("D04-B archive key format invalid."); }
            if (key.Length != 32)
                throw new InvalidOperationException("D04-B archive requires a 256-bit key.");
            services.AddSingleton<IPayOsD04ProtectedBankEvidenceArchive>(
                new PayOsD04DisposableEncryptedArchive(archiveRoot, key));
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        }

        // A configured database and ABP sign-in are insufficient:
        // financial review previously accepted a CLIENT-PROVIDED archive
        // label without retaining the bank file. Fail at startup if a
        // separately reviewed, server-controlled archival backend has
        // not been explicitly registered by an authorized staging module.
        // No such backend is registered by default, even in staging.
        if (!services.Any(s =>
                s.ServiceType == typeof(IPayOsD04ProtectedBankEvidenceArchive)))
            throw new InvalidOperationException(
                "D04-B activation DENIED: protected bank evidence archive is not registered.");

        services.AddDbContext<PayOsPaymentMigrationDbContext>(
            options => options.UseNpgsql(stagingD04,
                db => db.MigrationsHistoryTable("__EFMigrationsHistory_PayOs")));
        services.AddScoped<PayOsD04BStagingOrchestrator>();
        services.AddScoped<IPayOsD04ManualEvidenceService>(sp =>
            sp.GetRequiredService<PayOsD04BStagingOrchestrator>());
        services.AddScoped<IPayOsD04ReviewedPostingCoordinator>(sp =>
            sp.GetRequiredService<PayOsD04BStagingOrchestrator>());
        // No hosted worker, raw writer, permissive authority, live
        // payOS API client, migration or tenant-facing endpoint is added.
    }

    private static bool ExactD04BDisposableDatabase(string? input, string name)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        try
        {
            var db = new NpgsqlConnectionStringBuilder(input);
            return db.Host is ("localhost" or "127.0.0.1") &&
                   db.Port == 15432 &&
                   string.Equals(db.Database, name, StringComparison.Ordinal) &&
                   db.Username == "postgres" &&
                   db.Password == "disposable-only";
        }
        catch (ArgumentException) { return false; }
    }

    private static bool HaveTwoIsolatedSliceADatabases(
        string? buildingConnection, string? identityConnection)
    {
        if (!IsIsolatedSliceADatabase(buildingConnection) ||
            !IsIsolatedSliceADatabase(identityConnection))
            return false;

        // Sharing one staging database violates the isolation runbook:
        // Identity and BuildingManagement must not share one schema/owner
        // merely because both connection strings have a safe-looking prefix.
        var building = new NpgsqlConnectionStringBuilder(buildingConnection);
        var identity = new NpgsqlConnectionStringBuilder(identityConnection);
        return !string.Equals(building.Database, identity.Database,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIsolatedSliceADatabase(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return false;
        try
        {
            var parsed = new NpgsqlConnectionStringBuilder(connectionString);
            return parsed.Database?.StartsWith(
                       "bm_slicea_staging_", StringComparison.OrdinalIgnoreCase) == true &&
                   parsed.Database.Length > "bm_slicea_staging_".Length &&
                   !string.IsNullOrWhiteSpace(parsed.Host) &&
                   !string.IsNullOrWhiteSpace(parsed.Username);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void ConfigureStudio(IHostEnvironment hostingEnvironment)
    {
        if (hostingEnvironment.IsProduction())
        {
            Configure<AbpStudioClientOptions>(options =>
            {
                options.IsLinkEnabled = false;
            });
        }
    }

    private void ConfigureAuthentication(ServiceConfigurationContext context)
    {
        context.Services.ForwardIdentityAuthenticationForBearer(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        context.Services.Configure<AbpClaimsPrincipalFactoryOptions>(options =>
        {
            options.IsDynamicClaimsEnabled = true;
        });
    }

    private void ConfigureUrls(IConfiguration configuration)
    {
        Configure<AppUrlOptions>(options =>
        {
            options.Applications["MVC"].RootUrl = configuration["App:SelfUrl"];
            options.RedirectAllowedUrls.AddRange(configuration["App:RedirectAllowedUrls"]?.Split(',') ?? Array.Empty<string>());
        });
    }

    private void ConfigureBundles(IHostEnvironment hostingEnvironment)
    {
        Configure<AbpBundlingOptions>(options =>
        {
            options.StyleBundles.Configure(
                BasicThemeBundles.Styles.Global,
                bundle =>
                {
                    bundle.AddFiles("/global-styles.css");
                }
            );

            options.ScriptBundles.Configure(
                BasicThemeBundles.Scripts.Global,
                bundle =>
                {
                    bundle.AddFiles("/global-scripts.js");
                    // Never publish or autofill administrator passwords in client assets.
                }
            );
        });
    }


    private void ConfigureVirtualFileSystem(ServiceConfigurationContext context)
    {
        var hostingEnvironment = context.Services.GetHostingEnvironment();

        if (hostingEnvironment.IsDevelopment())
        {
            Configure<AbpVirtualFileSystemOptions>(options =>
            {
                options.FileSets.ReplaceEmbeddedByPhysical<NgocTrangHouseManagementSystemDomainSharedModule>(Path.Combine(hostingEnvironment.ContentRootPath, $"..{Path.DirectorySeparatorChar}NgocTrangHouseManagementSystem.Domain.Shared"));
                options.FileSets.ReplaceEmbeddedByPhysical<NgocTrangHouseManagementSystemDomainModule>(Path.Combine(hostingEnvironment.ContentRootPath, $"..{Path.DirectorySeparatorChar}NgocTrangHouseManagementSystem.Domain"));
                options.FileSets.ReplaceEmbeddedByPhysical<NgocTrangHouseManagementSystemApplicationContractsModule>(Path.Combine(hostingEnvironment.ContentRootPath, $"..{Path.DirectorySeparatorChar}NgocTrangHouseManagementSystem.Application.Contracts"));
                options.FileSets.ReplaceEmbeddedByPhysical<NgocTrangHouseManagementSystemApplicationModule>(Path.Combine(hostingEnvironment.ContentRootPath, $"..{Path.DirectorySeparatorChar}NgocTrangHouseManagementSystem.Application"));
            });
        }
    }

    private void ConfigureConventionalControllers()
    {
        Configure<AbpAspNetCoreMvcOptions>(options =>
        {
            options.ConventionalControllers.Create(typeof(NgocTrangHouseManagementSystemApplicationModule).Assembly);
        });
    }

    private static void ConfigureSwagger(
    ServiceConfigurationContext context,
    IConfiguration configuration)
    {
        context.Services.AddAbpSwaggerGenWithOidc(
            configuration["AuthServer:Authority"]!,
            ["NgocTrangHouseManagementSystem"],
            [AbpSwaggerOidcFlows.AuthorizationCode],
            null,
            options =>
            {
                options.SwaggerDoc(
                    "v1",
                    new OpenApiInfo
                    {
                        Title =
                            "NgocTrangHouseManagementSystem API",
                        Version = "v1"
                    }
                );

                options.DocInclusionPredicate(
                    (docName, description) => true
                );

                options.CustomSchemaIds(
                    type => type.FullName
                );

                options.OperationFilter<
                    ApiResponseOperationFilter
                >();
            });
    }

    private void ConfigureCors(ServiceConfigurationContext context, IConfiguration configuration)
    {
        context.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(builder =>
            {
                builder
                    .WithOrigins(
                        configuration["App:CorsOrigins"]?
                            .Split(",", StringSplitOptions.RemoveEmptyEntries)
                            .Select(o => o.Trim().RemovePostFix("/"))
                            .ToArray() ?? Array.Empty<string>()
                    )
                    .WithAbpExposedHeaders()
                    .SetIsOriginAllowedToAllowWildcardSubdomains()
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
            });
        });
    }

    private void ConfigureHealthChecks(ServiceConfigurationContext context)
    {
        context.Services.AddNgocTrangHouseManagementSystemHealthChecks();
    }

    private void ConfigureErrorHttpStatusCodeMappings()
    {
        Configure<AbpExceptionHttpStatusCodeOptions>(options =>
        {
            options.Map(
    BuildingManagementErrorCodes.InvalidTenantPhoneNumber,
    HttpStatusCode.BadRequest
);

            options.Map(
                BuildingManagementErrorCodes.InvalidTenantCitizenId,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.TenantIdentityMustBeCitizenId,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.TenantPhoneNumberAlreadyExists,
                HttpStatusCode.Conflict
            );

            options.Map(
    BuildingManagementErrorCodes.PaymentNumberAlreadyExists,
    HttpStatusCode.Conflict
);

            options.Map(
                BuildingManagementErrorCodes.PaymentCannotBeCompleted,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.PaymentCannotBeCancelled,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.TransactionReferenceAlreadyExists,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.RoomReservationNotPayable,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.ReservationDepositExceedsRemaining,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.ReservationDepositAlreadyFullyPaid,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.ActiveRenewalHoldBlocksReservationDeposit,
                HttpStatusCode.Conflict
            );

            options.Map(
    BuildingManagementErrorCodes.InvalidPaymentNumber,
    HttpStatusCode.BadRequest
);

            options.Map(
                BuildingManagementErrorCodes.InvalidPaymentAmount,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.InvalidPaymentMethod,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.InvalidPaymentChannel,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.InvalidPaymentMethodChannel,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.TransactionReferenceRequired,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.ReservationDepositBelowMinimum,
                HttpStatusCode.BadRequest
            );

            options.Map(
    BuildingManagementErrorCodes.PaymentNotFound,
    HttpStatusCode.NotFound
);

            options.Map(
    BuildingManagementErrorCodes
        .InvalidMinimumReservationDepositAmount,
    HttpStatusCode.BadRequest
);

            options.Map(
    BuildingManagementErrorCodes.RoomReservationNotFound,
    HttpStatusCode.NotFound
);

            options.Map(
    BuildingManagementErrorCodes.RoomReservationNumberAlreadyExists,
    HttpStatusCode.Conflict
);

            options.Map(
                BuildingManagementErrorCodes.RoomNotAvailableForReservation,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.RoomAlreadyReserved,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.RoomReservationAlreadyExistsForTenant,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.RoomReservationCannotBeCancelled,
                HttpStatusCode.Conflict
            );

            options.Map(
    BuildingManagementErrorCodes.InvalidRoomReservationNumber,
    HttpStatusCode.BadRequest
);

            options.Map(
                BuildingManagementErrorCodes.InvalidExpectedMoveInDate,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.InvalidQuotedMonthlyRent,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.InvalidRequiredDepositAmount,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.InvalidReservationCancellationReason,
                HttpStatusCode.BadRequest
            );
            options.Map(
    BuildingManagementErrorCodes.ContractRenewalHoldRequired,
    HttpStatusCode.Conflict
);

            options.Map(
                BuildingManagementErrorCodes.ContractRenewalHoldNotActive,
                HttpStatusCode.Conflict
            );

            options.Map(
    BuildingManagementErrorCodes.ContractRenewalHoldNotFound,
    HttpStatusCode.NotFound
);

            options.Map(
                BuildingManagementErrorCodes.ContractRenewalHoldAlreadyExists,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.ContractRenewalHoldCannotBeCreated,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.ContractRenewalHoldCannotBeCancelled,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.ContractRenewalHoldExpired,
                HttpStatusCode.Conflict
            );

            options.Map(
    BuildingManagementErrorCodes.ContractCannotBeSigned,
    HttpStatusCode.Conflict
);

            options.Map(
                BuildingManagementErrorCodes.ContractCannotBeRenewed,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.ContractAlreadyRenewed,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.InvalidRenewalContractPeriod,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.RoomHasOverlappingCommittedContract,
                HttpStatusCode.Conflict
            );

            options.Map(
    BuildingManagementErrorCodes.InvalidBillingYear,
    HttpStatusCode.BadRequest
);

            options.Map(
                BuildingManagementErrorCodes.InvalidBillingMonth,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.InvalidReadingDate,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.InvalidCurrentReading,
                HttpStatusCode.BadRequest
            );

            options.Map(
    BuildingManagementErrorCodes.MeterReadingNotFound,
    HttpStatusCode.NotFound
);

            options.Map(
    BuildingManagementErrorCodes.UtilityMeterInactive,
    HttpStatusCode.Conflict
);

            options.Map(
                BuildingManagementErrorCodes.MeterReadingAlreadyExists,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.MeterReadingPeriodMustBeAfterLatest,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.OnlyLatestMeterReadingCanBeUpdated,
                HttpStatusCode.Conflict
            );

            options.Map(
    BuildingManagementErrorCodes.RoomHasActiveUtilityMeter,
    HttpStatusCode.Conflict
);

            options.Map(
    BuildingManagementErrorCodes.UtilityMeterNotFound,
    HttpStatusCode.NotFound
);

            options.Map(
                BuildingManagementErrorCodes.InvalidMeterCode,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.InvalidInitialMeterReading,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.MeterCodeAlreadyExists,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.ActiveUtilityMeterAlreadyExists,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.UtilityMeterAlreadyInactive,
                HttpStatusCode.Conflict
            );

            options.Map(
    BuildingManagementErrorCodes.UtilityRateNotFound,
    HttpStatusCode.NotFound
);

            options.Map(
                BuildingManagementErrorCodes.InvalidUtilityType,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.InvalidUtilityUnitPrice,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.InvalidUtilityRatePeriod,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.UtilityRatePeriodOverlaps,
                HttpStatusCode.Conflict
            );

            options.Map(
    BuildingManagementErrorCodes.RoomHasActiveContract,
    HttpStatusCode.Conflict
);

            options.Map(
                BuildingManagementErrorCodes.TenantHasActiveContract,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.TenantHasContractHistory,
                HttpStatusCode.Conflict
            );

            options.Map(
    BuildingManagementErrorCodes.ContractCannotBeActivated,
    HttpStatusCode.Conflict
);

            options.Map(
                BuildingManagementErrorCodes.ContractCannotBeEnded,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.ContractCannotBeCancelled,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.ContractRequiresTenant,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.ContractRequiresPrimaryTenant,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.RoomAlreadyHasActiveContract,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.RoomNotAvailableForRent,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.ContractCanOnlyBeModifiedWhenDraft,
                HttpStatusCode.Conflict
            );

            options.Map(
    BuildingManagementErrorCodes.ContractNotFound,
    HttpStatusCode.NotFound
);

            options.Map(
                BuildingManagementErrorCodes.ContractNumberAlreadyExists,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.InvalidContractPeriod,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.InvalidMonthlyRent,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.TenantAlreadyInContract,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.ContractTenantNotFound,
                HttpStatusCode.NotFound
            );

            options.Map(
                BuildingManagementErrorCodes.ContractAlreadyHasPrimaryTenant,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.InvalidContractTenantRole,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.RoomNumberAlreadyExists,
                HttpStatusCode.Conflict
            );

            options.Map(
    BuildingManagementErrorCodes.TenantNotFound,
    HttpStatusCode.NotFound
);

            options.Map(
                BuildingManagementErrorCodes.TenantIdentityAlreadyExists,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.InvalidTenantIdentityInformation,
                HttpStatusCode.BadRequest
            );

            options.Map(
    BuildingManagementErrorCodes.RoomNotFound,
    HttpStatusCode.NotFound
);

            options.Map(
    BuildingManagementErrorCodes.FloorNotFound,
    HttpStatusCode.NotFound
);

            options.Map(
    BuildingManagementErrorCodes.FloorNumberAlreadyExists,
    HttpStatusCode.Conflict
);

            options.Map(
                BuildingManagementErrorCodes.InvalidRoomNumber,
                HttpStatusCode.BadRequest
            );

            options.Map(
                BuildingManagementErrorCodes.RoomNumberDoesNotMatchFloor,
                HttpStatusCode.BadRequest
            );

            options.Map(
    BuildingManagementErrorCodes.BuildingNotFound,
    HttpStatusCode.NotFound
);

            options.Map(
                BuildingManagementErrorCodes.BuildingHasFloors,
                HttpStatusCode.Conflict
            );

            options.Map(
                BuildingManagementErrorCodes.FloorHasRooms,
                HttpStatusCode.Conflict
            );
        });
    }


    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var app = context.GetApplicationBuilder();
        var env = context.GetEnvironment();

        app.UseForwardedHeaders();

        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.UseAbpRequestLocalization();

        if (!env.IsDevelopment())
        {
            app.UseErrorPage();
        }

        app.UseRouting();
        app.MapAbpStaticAssets();
        app.UseAbpStudioLink();
        app.UseAbpSecurityHeaders();
        app.UseCors();
        app.UseAuthentication();
        app.UseAbpOpenIddictValidation();

        if (MultiTenancyConsts.IsEnabled)
        {
            app.UseMultiTenancy();
        }

        app.UseUnitOfWork();
        app.UseDynamicClaims();
        app.UseAuthorization();

        app.UseSwagger();
        app.UseAbpSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "NgocTrangHouseManagementSystem API");

            var configuration = context.ServiceProvider.GetRequiredService<IConfiguration>();
            options.OAuthClientId(configuration["AuthServer:SwaggerClientId"]);
        });
        app.UseAuditing();
        app.UseAbpSerilogEnrichers();
        app.UseConfiguredEndpoints();
    }
}
