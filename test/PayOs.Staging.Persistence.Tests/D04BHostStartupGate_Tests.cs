using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using BuildingManagement.EntityFrameworkCore.PayOsMigrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NgocTrangHouseManagementSystem.Payments;
using Volo.Abp.Modularity;
using Xunit;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

/// <summary>
/// D04B_GATE: the Host startup gate must fail closed BEFORE any D04-B
/// financial service exists. In-memory only: no database, no provider build,
/// no environment variables, no secrets. Mirrors STAGEAPI03 reflection tests.
/// </summary>
public sealed class D04BHostStartupGate_Tests
{
    private const string Activation = "I_ACCEPT_DISPOSABLE_D04B_DUAL_AUTH_ONLY";
    private const string Loopback =
        "Host=127.0.0.1;Port=15432;Username=postgres;Password=disposable-only;Database=";
    private const string StagingMessage =
        "D04-B requires dedicated no-merchant, three-DB disposable staging.";
    private const string TripleDbMessage =
        "D04-B rejects anything but three exact disposable PostgreSQL databases.";
    private const string ArchiveRootKeyMessage =
        "D04-B disposable archive requires absolute root and operator key.";
    private const string ArchiveFormatMessage =
        "D04-B archive key format invalid.";
    private const string ArchiveKeySizeMessage =
        "D04-B archive requires a 256-bit key.";
    private const string ArchiveDeniedMessage =
        "D04-B activation DENIED: protected bank evidence archive is not registered.";

    [Fact]
    public void D04B_GATE01_Exact_disposable_database_requires_loopback_15432_exact_name_postgres_and_disposable_password()
    {
        // Exact startup guard by reflection: no connection attempt is made.
        var gate = typeof(NgocTrangHouseManagementSystemHttpApiHostModule).GetMethod(
            "ExactD04BDisposableDatabase",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(gate);
        bool Allowed(string? connection, string name)
            => Assert.IsType<bool>(gate!.Invoke(null, new object?[] { connection, name }));

        const string identity = Loopback + "payos_d04_identity_test";
        const string legacy = Loopback + "payos_d04_legacy_test";
        const string staging = Loopback + "payos_ef_migration_test";

        Assert.True(Allowed(identity, "payos_d04_identity_test"));
        Assert.True(Allowed(legacy, "payos_d04_legacy_test"));
        Assert.True(Allowed(staging, "payos_ef_migration_test"));
        Assert.True(Allowed(identity.Replace("127.0.0.1", "localhost"),
            "payos_d04_identity_test"));

        // A slot must hold ITS exact database, not one of the other two.
        Assert.False(Allowed(identity, "payos_d04_legacy_test"));
        Assert.False(Allowed(legacy, "payos_ef_migration_test"));
        Assert.False(Allowed(staging, "payos_d04_identity_test"));

        // The persistent user database and any non-15432 port are excluded.
        Assert.False(Allowed(
            "Host=127.0.0.1;Port=5432;Username=postgres;Password=disposable-only;Database=payos_d04_identity_test",
            "payos_d04_identity_test"));
        Assert.False(Allowed(
            "Host=127.0.0.1;Username=postgres;Password=disposable-only;Database=payos_d04_identity_test",
            "payos_d04_identity_test"));
        Assert.False(Allowed(
            "Host=192.168.1.10;Port=15432;Username=postgres;Password=disposable-only;Database=payos_d04_identity_test",
            "payos_d04_identity_test"));

        // Role and password must be the disposable pair.
        Assert.False(Allowed(
            "Host=127.0.0.1;Port=15432;Username=app;Password=disposable-only;Database=payos_d04_identity_test",
            "payos_d04_identity_test"));
        Assert.False(Allowed(
            "Host=127.0.0.1;Port=15432;Username=postgres;Password=real-password;Database=payos_d04_identity_test",
            "payos_d04_identity_test"));

        Assert.False(Allowed(null, "payos_d04_identity_test"));
        Assert.False(Allowed("", "payos_d04_identity_test"));
        Assert.False(Allowed("   ", "payos_d04_identity_test"));
        Assert.False(Allowed("SomeRandomKeyword=1", "payos_d04_identity_test"));
    }

    [Fact]
    public void D04B_GATE02_Default_off_registers_no_D04B_financial_service()
    {
        var services = new ServiceCollection();
        Gate(new Dictionary<string, string?>(), Environments.Production, services);

        Assert.DoesNotContain(services, IsD04BServices);
    }

    [Fact]
    public void D04B_GATE03_Requires_staging_activation_phrase_and_merchant_off()
    {
        // Production or Development can never enable D04-B, even with the
        // exact activation phrase and the three exact databases.
        AssertGateThrows(StagingMessage, Valid(), Environments.Production);
        AssertGateThrows(StagingMessage, Valid(), Environments.Development);

        var wrongActivation = Valid();
        wrongActivation["BuildingManagement:PayOsD04B:Activation"] = "I_ACCEPT_ANYTHING";
        AssertGateThrows(StagingMessage, wrongActivation, Environments.Staging);

        var missingActivation = Valid();
        missingActivation.Remove("BuildingManagement:PayOsD04B:Activation");
        AssertGateThrows(StagingMessage, missingActivation, Environments.Staging);

        // D04-B must never coexist with the live-merchant staging checkout.
        var merchant = Valid();
        merchant["BuildingManagement:PayOsStaging:Enabled"] = "true";
        AssertGateThrows(StagingMessage, merchant, Environments.Staging);
    }

    [Fact]
    public void D04B_GATE04_Rejects_anything_but_the_three_exact_disposable_databases()
    {
        string[] slots =
        [
            "ConnectionStrings:Default",
            "ConnectionStrings:BuildingManagement",
            "ConnectionStrings:PayOsD04BStaging"
        ];

        // Persistent 5432 database names in every slot.
        foreach (var slot in slots)
        {
            var config = Valid();
            config[slot] = config[slot]!.Replace("Port=15432", "Port=5432");
            AssertGateThrows(TripleDbMessage, config, Environments.Staging);
        }

        // The real user database and a swapped slot are refused.
        var userDb = Valid();
        userDb["ConnectionStrings:BuildingManagement"] =
            Loopback + "NgocTrangDb";
        AssertGateThrows(TripleDbMessage, userDb, Environments.Staging);

        var swapped = Valid();
        swapped["ConnectionStrings:Default"] = Loopback + "payos_d04_legacy_test";
        AssertGateThrows(TripleDbMessage, swapped, Environments.Staging);

        // A missing slot is refused, not defaulted.
        var missing = Valid();
        missing.Remove("ConnectionStrings:PayOsD04BStaging");
        AssertGateThrows(TripleDbMessage, missing, Environments.Staging);

        // Non-loopback host and non-postgres role are refused.
        var remote = Valid();
        remote["ConnectionStrings:Default"] = Loopback
            .Replace("127.0.0.1", "db.example.com") + "payos_d04_identity_test";
        AssertGateThrows(TripleDbMessage, remote, Environments.Staging);

        var role = Valid();
        role["ConnectionStrings:BuildingManagement"] = Loopback
            .Replace("Username=postgres", "Username=app") + "payos_d04_legacy_test";
        AssertGateThrows(TripleDbMessage, role, Environments.Staging);
    }

    [Fact]
    public void D04B_GATE05_Archive_requires_absolute_root_and_operator_key()
    {
        const string root = "BuildingManagement:PayOsD04B:DisposableArchive:Root";
        const string key = "BuildingManagement:PayOsD04B:DisposableArchive:KeyBase64";
        var validKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var absoluteRoot = Path.Combine(Path.GetTempPath(),
            "d04b-gate-" + Guid.NewGuid().ToString("N"));

        try
        {
            var missingRoot = Archive(validKey, null);
            AssertGateThrows(ArchiveRootKeyMessage, missingRoot, Environments.Staging);

            var relativeRoot = Archive(validKey, "relative" + Path.DirectorySeparatorChar + "vault");
            AssertGateThrows(ArchiveRootKeyMessage, relativeRoot, Environments.Staging);

            var missingKey = Archive(null, absoluteRoot);
            AssertGateThrows(ArchiveRootKeyMessage, missingKey, Environments.Staging);

            var blankKey = Valid();
            blankKey["BuildingManagement:PayOsD04B:DisposableArchive:Enabled"] = "true";
            blankKey[root] = absoluteRoot;
            blankKey[key] = "   ";
            AssertGateThrows(ArchiveRootKeyMessage, blankKey, Environments.Staging);

            var malformed = Archive("not-base64-!!!", absoluteRoot);
            AssertGateThrows(ArchiveFormatMessage, malformed, Environments.Staging);

            var shortKey = Archive(Convert.ToBase64String(new byte[16]), absoluteRoot);
            AssertGateThrows(ArchiveKeySizeMessage, shortKey, Environments.Staging);

            // Fail-closed means no directory was created for rejected keys.
            Assert.False(Directory.Exists(absoluteRoot));
        }
        finally
        {
            if (Directory.Exists(absoluteRoot))
                Directory.Delete(absoluteRoot, true);
        }
    }

    [Fact]
    public void D04B_GATE06_Denies_activation_without_registered_archive()
    {
        // Three exact databases and the activation phrase are still not
        // enough: without a server-controlled archive the Host must refuse
        // to start the D04-B financial services at all.
        var config = Valid();
        config["BuildingManagement:PayOsD04B:DisposableArchive:Enabled"] = "false";
        AssertGateThrows(ArchiveDeniedMessage, config, Environments.Staging);

        // An archive descriptor of an unrelated service type does not count.
        var services = new ServiceCollection();
        services.AddSingleton(
            new PayOsD04DisposableEncryptedArchiveFactoryStub());
        var exception = Assert.Throws<InvalidOperationException>(() =>
            GateWith(config, Environments.Staging, services));
        Assert.Equal(ArchiveDeniedMessage, exception.Message);
    }

    [Fact]
    public void D04B_GATE07_Exact_triple_database_with_archive_registers_only_the_d04b_services()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "d04b-gate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = Archive(
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), root);
            var services = new ServiceCollection();
            GateWith(config, Environments.Staging, services);

            var archive = Assert.Single(services,
                d => d.ServiceType == typeof(IPayOsD04ProtectedBankEvidenceArchive));
            Assert.Equal(ServiceLifetime.Singleton, archive.Lifetime);
            Assert.IsType<PayOsD04DisposableEncryptedArchive>(
                archive.ImplementationInstance);

            Assert.Contains(services, d => d.ServiceType == typeof(PayOsD04BStagingOrchestrator)
                && d.Lifetime == ServiceLifetime.Scoped);
            Assert.Contains(services,
                d => d.ServiceType == typeof(IPayOsD04ManualEvidenceService)
                    && d.Lifetime == ServiceLifetime.Scoped);
            Assert.Contains(services,
                d => d.ServiceType == typeof(IPayOsD04ReviewedPostingCoordinator)
                    && d.Lifetime == ServiceLifetime.Scoped);
            Assert.Contains(services,
                d => d.ServiceType == typeof(PayOsPaymentMigrationDbContext));

            // The gate must not add a background worker, live provider
            // client or migration trigger of its own.
            Assert.DoesNotContain(services,
                d => d.ServiceType == typeof(IHostedService));

            // The disposable archive directory is created only here, on the
            // fully validated path.
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void D04B_GATE08_Host_ConfigureServices_delegates_fail_closed_to_the_D04B_gate()
    {
        // Prove the real Host module still calls the gate: the production
        // path must throw the staging message before any later Configure.
        var production = new ServiceCollection();
        RegisterContext(production, Valid(), Environments.Production);
        var module = new NgocTrangHouseManagementSystemHttpApiHostModule();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            module.ConfigureServices(new ServiceConfigurationContext(production)));
        Assert.Equal(StagingMessage, exception.Message);

        // And a staging run with a non-exact database must also reach and
        // fail inside the gate.
        var wrongDb = Valid();
        wrongDb["ConnectionStrings:PayOsD04BStaging"] =
            Loopback + "payos_d04_legacy_test";
        var staging = new ServiceCollection();
        RegisterContext(staging, wrongDb, Environments.Staging);
        var stagingModule = new NgocTrangHouseManagementSystemHttpApiHostModule();
        exception = Assert.Throws<InvalidOperationException>(() =>
            stagingModule.ConfigureServices(new ServiceConfigurationContext(staging)));
        Assert.Equal(TripleDbMessage, exception.Message);
    }

    private static Dictionary<string, string?> Valid() => new()
    {
        ["BuildingManagement:PayOsD04B:Enabled"] = "true",
        ["BuildingManagement:PayOsD04B:Activation"] = Activation,
        ["ConnectionStrings:Default"] = Loopback + "payos_d04_identity_test",
        ["ConnectionStrings:BuildingManagement"] = Loopback + "payos_d04_legacy_test",
        ["ConnectionStrings:PayOsD04BStaging"] = Loopback + "payos_ef_migration_test"
    };

    private static Dictionary<string, string?> Archive(string? keyBase64, string? root)
    {
        var config = Valid();
        config["BuildingManagement:PayOsD04B:DisposableArchive:Enabled"] = "true";
        config["BuildingManagement:PayOsD04B:DisposableArchive:Root"] = root;
        config["BuildingManagement:PayOsD04B:DisposableArchive:KeyBase64"] = keyBase64;
        return config;
    }

    private static void AssertGateThrows(string expectedMessage,
        Dictionary<string, string?> values, string environment)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Gate(values, environment));
        Assert.Equal(expectedMessage, exception.Message);
    }

    private static void Gate(Dictionary<string, string?> values,
        string environment, IServiceCollection? services = null)
        => GateWith(values, environment, services ?? new ServiceCollection());

    private static void GateWith(Dictionary<string, string?> values,
        string environment, IServiceCollection services)
        => NgocTrangHouseManagementSystemHttpApiHostModule
            .ConfigurePayOsD04BIfEnabled(
                new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
                new StubHostEnvironment { EnvironmentName = environment },
                services);

    private static void RegisterContext(IServiceCollection services,
        Dictionary<string, string?> values, string environment)
    {
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        // ABP's GetHostingEnvironment() resolves IWebHostEnvironment.
        var host = new StubHostEnvironment { EnvironmentName = environment };
        services.AddSingleton<IWebHostEnvironment>(host);
        services.AddSingleton<IHostEnvironment>(host);
    }

    private static bool IsD04BServices(ServiceDescriptor descriptor)
        => descriptor.ServiceType == typeof(IPayOsD04ProtectedBankEvidenceArchive) ||
           descriptor.ServiceType == typeof(PayOsD04BStagingOrchestrator) ||
           descriptor.ServiceType == typeof(IPayOsD04ManualEvidenceService) ||
           descriptor.ServiceType == typeof(IPayOsD04ReviewedPostingCoordinator) ||
           descriptor.ServiceType == typeof(PayOsPaymentMigrationDbContext);

    private sealed class StubHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "D04B.Gate.Test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; }
            = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; }
            = new NullFileProvider();
    }

    /// <summary>Distinct type so the DENIED test proves type equality only.</summary>
    private sealed class PayOsD04DisposableEncryptedArchiveFactoryStub
    {
    }
}