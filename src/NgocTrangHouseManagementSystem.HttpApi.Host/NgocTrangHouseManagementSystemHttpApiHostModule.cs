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
            context.Services.AddTransient<SliceCStagingUtilityRates>();
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


        if (!configuration.GetValue<bool>("App:DisablePII"))
        {
            Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = true;
            Microsoft.IdentityModel.Logging.IdentityModelEventSource.LogCompleteSecurityArtifact = true;
        }

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
                    if (hostingEnvironment.IsDevelopment())
                    {
                        bundle.AddFiles("/dev-login-helper.js");
                    }
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
