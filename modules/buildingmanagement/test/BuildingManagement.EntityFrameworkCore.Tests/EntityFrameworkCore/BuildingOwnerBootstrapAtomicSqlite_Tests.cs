using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.EntityFrameworkCore.EntityConfigurations;
using BuildingManagement.EntityFrameworkCore.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Authorization;
using Xunit;

namespace BuildingManagement.EntityFrameworkCore;

/// <summary>
/// No actual BuildingManagementDbContext grant mapping or production DB.
/// The proposed writer is instantiated manually over a disposable private
/// SQLite schema and proves two-row insert + rollback semantics.
/// </summary>
public sealed class BuildingOwnerBootstrapAtomicSqlite_Tests
{
    [Fact]
    public async Task BOOTDB01_Create_Building_And_First_Owner_Together()
    {
        using var store = await TestStore.CreateAsync();
        var ownerId = Guid.NewGuid();
        Guid buildingId;

        await using (var db = store.Open())
        {
            var writer = new EfBuildingOwnerBootstrapAtomicWriter(db);
            var newBuilding = await writer.CreateWithOwnerAtomicallyAsync(
                ownerId, "New building", "Test-only address", "First owner");
            buildingId = newBuilding.Id;
        }

        await using (var verify = store.Open())
        {
            var building = await verify.Buildings.SingleAsync();
            var grant = await verify.Grants.SingleAsync();
            Assert.Equal(buildingId, building.Id);
            Assert.Equal(building.Id, grant.BuildingId);
            Assert.Equal(ownerId, grant.OperatorUserId);
            Assert.Equal(ownerId, grant.GrantedByUserId);
            Assert.Null(grant.RevokedAtUtc);
            Assert.True(grant.IsEffectiveAt(DateTime.UtcNow));
        }
    }

    [Fact]
    public async Task BOOTDB02_Grant_Insert_Failure_Rolls_Back_Inserted_Building()
    {
        using var store = await TestStore.CreateAsync();

        // Simulates a real relational DML failure AFTER the writer's first
        // SaveChanges inserted Building but BEFORE the grant can commit.
        await using (var guard = store.Connection.CreateCommand())
        {
            guard.CommandText =
                @"CREATE TRIGGER RejectBootstrapGrant
                  BEFORE INSERT ON BuildingManagementOperatorBuildingGrants
                  BEGIN
                    SELECT RAISE(ABORT, 'forced first owner grant failure');
                  END;";
            await guard.ExecuteNonQueryAsync();
        }

        await using (var db = store.Open())
        {
            var writer = new EfBuildingOwnerBootstrapAtomicWriter(db);
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                writer.CreateWithOwnerAtomicallyAsync(
                    Guid.NewGuid(), "Must not exist", "Test-only address", null));
        }

        await using (var verify = store.Open())
        {
            Assert.Equal(0, await verify.Buildings.CountAsync());
            Assert.Equal(0, await verify.Grants.CountAsync());
        }
    }

    [Fact]
    public async Task BOOTDB03_Unknown_Owner_Is_Denied_Before_Any_Insert()
    {
        using var store = await TestStore.CreateAsync();
        await using (var db = store.Open())
        {
            var writer = new EfBuildingOwnerBootstrapAtomicWriter(db);
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                writer.CreateWithOwnerAtomicallyAsync(
                    Guid.Empty, "Invalid", "Test-only address", null));
        }
        await using (var verify = store.Open())
        {
            Assert.Equal(0, await verify.Buildings.CountAsync());
            Assert.Equal(0, await verify.Grants.CountAsync());
        }
    }

    [Fact]
    public async Task BOOTDB04_Two_Bootstraps_Create_Two_Independent_Owner_Grants()
    {
        using var store = await TestStore.CreateAsync();
        var owner = Guid.NewGuid();

        await using (var db = store.Open())
        {
            var writer = new EfBuildingOwnerBootstrapAtomicWriter(db);
            await writer.CreateWithOwnerAtomicallyAsync(
                owner, "Building A", "Address A", null);
            await writer.CreateWithOwnerAtomicallyAsync(
                owner, "Building B", "Address B", null);
        }

        await using (var verify = store.Open())
        {
            Assert.Equal(2, await verify.Buildings.CountAsync());
            Assert.Equal(2, await verify.Grants.CountAsync());
            Assert.Equal(2, await verify.Grants
                .Where(grant => grant.OperatorUserId == owner)
                .Select(grant => grant.BuildingId).Distinct().CountAsync());
        }
    }

    private sealed class TestStore : IDisposable
    {
        public SqliteConnection Connection { get; }
        private readonly DbContextOptions<TestDbContext> _options;

        private TestStore(SqliteConnection connection)
        {
            Connection = connection;
            _options = new DbContextOptionsBuilder<TestDbContext>()
                .UseSqlite(connection).Options;
        }

        public static async Task<TestStore> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var store = new TestStore(connection);
            await using (var db = store.Open())
                await db.Database.EnsureCreatedAsync();
            return store;
        }

        public TestDbContext Open() => new(_options);

        public void Dispose() => Connection.Dispose();
    }

    private sealed class TestDbContext : DbContext
    {
        public TestDbContext(DbContextOptions<TestDbContext> options)
            : base(options) { }

        public DbSet<Building> Buildings => Set<Building>();
        public DbSet<BuildingOperatorAccessGrant> Grants
            => Set<BuildingOperatorAccessGrant>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new BuildingConfiguration());
            modelBuilder.ApplyConfiguration(
                new BuildingOperatorAccessGrantDesignConfiguration());
        }
    }
}
