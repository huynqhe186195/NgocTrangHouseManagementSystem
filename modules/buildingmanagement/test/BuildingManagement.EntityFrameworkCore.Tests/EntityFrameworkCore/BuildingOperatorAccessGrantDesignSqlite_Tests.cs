using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.EntityFrameworkCore.EntityConfigurations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildingManagement.EntityFrameworkCore;

/// <summary>
/// Experimental SQLite schema for the membership RFC ONLY. This DbContext
/// is private to this test and has no connection to the application DbContext,
/// migration chain, credentials or business data.
/// </summary>
public sealed class BuildingOperatorAccessGrantDesignSqlite_Tests
{
    private static readonly DateTime At =
        new(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task MEMBERDB01_Synthetic_Record_Persists_With_Audit_Facts()
    {
        using var store = await DesignStore.CreateAsync();
        var building = Guid.NewGuid();
        var user = Guid.NewGuid();
        var issuer = Guid.NewGuid();
        var id = Guid.NewGuid();

        await SeedBuildingAsync(store, building);
        await using (var db = store.Open())
        {
            db.Grants.Add(BuildingOperatorAccessGrant.Issue(
                id, user, building, issuer, At, At.AddDays(7)));
            await db.SaveChangesAsync();
        }

        await using (var db = store.Open())
        {
            var saved = await db.Grants.SingleAsync(x => x.Id == id);
            Assert.Equal(user, saved.OperatorUserId);
            Assert.Equal(building, saved.BuildingId);
            Assert.Equal(issuer, saved.GrantedByUserId);
            Assert.Equal(At, saved.GrantedAtUtc);
            Assert.Equal(At.AddDays(7), saved.ExpiresAtUtc);
            Assert.Null(saved.RevokedAtUtc);
            Assert.Equal(1, saved.RevisionNumber);
            Assert.True(saved.IsEffectiveAt(At.AddDays(6)));
            Assert.False(saved.IsEffectiveAt(At.AddDays(7)));
        }
    }

    [Fact]
    public async Task MEMBERDB02_Duplicate_Unrevoked_Membership_Fails_Regrant_After_Revoke()
    {
        using var store = await DesignStore.CreateAsync();
        var building = Guid.NewGuid();
        var user = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await SeedBuildingAsync(store, building);

        await using (var db = store.Open())
        {
            db.Grants.Add(BuildingOperatorAccessGrant.Issue(
                firstId, user, building, Guid.NewGuid(), At, null));
            await db.SaveChangesAsync();
        }

        await using (var db = store.Open())
        {
            db.Grants.Add(BuildingOperatorAccessGrant.Issue(
                secondId, user, building, Guid.NewGuid(),
                At.AddDays(1), null));
            await Assert.ThrowsAsync<DbUpdateException>(
                async () => await db.SaveChangesAsync());
        }

        await using (var db = store.Open())
        {
            var old = await db.Grants.SingleAsync(x => x.Id == firstId);
            old.Revoke(Guid.NewGuid(), At.AddDays(2));
            await db.SaveChangesAsync();
        }

        await using (var db = store.Open())
        {
            db.Grants.Add(BuildingOperatorAccessGrant.Issue(
                secondId, user, building, Guid.NewGuid(),
                At.AddDays(3), null));
            await db.SaveChangesAsync();
            Assert.Equal(2, await db.Grants.CountAsync());
            Assert.Equal(1, await db.Grants.CountAsync(x =>
                x.OperatorUserId == user &&
                x.BuildingId == building &&
                x.RevokedAtUtc == null));
        }
    }

    [Fact]
    public async Task MEMBERDB03_Unknown_Building_Foreign_Key_Fails()
    {
        using var store = await DesignStore.CreateAsync();
        await using var db = store.Open();
        db.Grants.Add(BuildingOperatorAccessGrant.Issue(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), At, null));

        await Assert.ThrowsAsync<DbUpdateException>(
            async () => await db.SaveChangesAsync());
    }

    [Fact]
    public async Task MEMBERDB04_Stale_Revocation_Revision_Cannot_Overwrite_Winner()
    {
        using var store = await DesignStore.CreateAsync();
        var building = Guid.NewGuid();
        var id = Guid.NewGuid();
        await SeedBuildingAsync(store, building);

        await using (var db = store.Open())
        {
            db.Grants.Add(BuildingOperatorAccessGrant.Issue(
                id, Guid.NewGuid(), building, Guid.NewGuid(), At, null));
            await db.SaveChangesAsync();
        }

        await using var winner = store.Open();
        await using var loser = store.Open();

        var first = await winner.Grants.SingleAsync(x => x.Id == id);
        var second = await loser.Grants.SingleAsync(x => x.Id == id);
        first.Revoke(Guid.NewGuid(), At.AddDays(1));
        await winner.SaveChangesAsync();

        second.Revoke(Guid.NewGuid(), At.AddDays(2));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            async () => await loser.SaveChangesAsync());

        await using var verify = store.Open();
        var saved = await verify.Grants.SingleAsync(x => x.Id == id);
        Assert.Equal(2, saved.RevisionNumber);
        Assert.Equal(At.AddDays(1), saved.RevokedAtUtc);
    }

    [Fact]
    public async Task MEMBERDB05_One_Operator_May_Have_Independent_Buildings()
    {
        using var store = await DesignStore.CreateAsync();
        var building1 = Guid.NewGuid();
        var building2 = Guid.NewGuid();
        var user = Guid.NewGuid();
        await SeedBuildingAsync(store, building1);
        await SeedBuildingAsync(store, building2);

        await using (var db = store.Open())
        {
            db.Grants.Add(BuildingOperatorAccessGrant.Issue(
                Guid.NewGuid(), user, building1, Guid.NewGuid(),
                At, At.AddDays(7)));
            db.Grants.Add(BuildingOperatorAccessGrant.Issue(
                Guid.NewGuid(), user, building2, Guid.NewGuid(),
                At, null));
            await db.SaveChangesAsync();
        }

        await using (var db = store.Open())
        {
            var allowed = await db.Grants
                .Where(x => x.OperatorUserId == user &&
                    x.GrantedAtUtc <= At.AddDays(8) &&
                    x.RevokedAtUtc == null &&
                    (x.ExpiresAtUtc == null ||
                     x.ExpiresAtUtc > At.AddDays(8)))
                .Select(x => x.BuildingId)
                .ToArrayAsync();

            Assert.Equal(new[] { building2 }, allowed);
        }
    }

    private static async Task SeedBuildingAsync(DesignStore store, Guid buildingId)
    {
        await using var db = store.Open();
        db.Buildings.Add(new Building(buildingId,
            "Membership design fixture", "SQLite memory only"));
        await db.SaveChangesAsync();
    }

    private sealed class DesignStore : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<DesignDbContext> _options;

        private DesignStore(SqliteConnection connection)
        {
            _connection = connection;
            _options = new DbContextOptionsBuilder<DesignDbContext>()
                .UseSqlite(connection).Options;
        }

        public static async Task<DesignStore> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var store = new DesignStore(connection);
            await using var db = store.Open();
            await db.Database.EnsureCreatedAsync();
            return store;
        }

        public DesignDbContext Open() => new(_options);

        public void Dispose() => _connection.Dispose();
    }

    private sealed class DesignDbContext : DbContext
    {
        public DesignDbContext(DbContextOptions<DesignDbContext> options)
            : base(options) { }

        public DbSet<Building> Buildings => Set<Building>();
        public DbSet<BuildingOperatorAccessGrant> Grants =>
            Set<BuildingOperatorAccessGrant>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new BuildingConfiguration());
            modelBuilder.ApplyConfiguration(
                new BuildingOperatorAccessGrantDesignConfiguration());
        }
    }
}
