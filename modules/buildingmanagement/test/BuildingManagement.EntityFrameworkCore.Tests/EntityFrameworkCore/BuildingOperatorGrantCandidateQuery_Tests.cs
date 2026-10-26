using System;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.EntityFrameworkCore.EntityConfigurations;
using BuildingManagement.EntityFrameworkCore.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildingManagement.EntityFrameworkCore;

/// <summary>
/// Tests the candidate SQL query against only a disposable SQLite database.
/// The production module DbContext does NOT map this grant table, and the
/// query is not registered as a production IBuildingOperatorGrantReader.
/// </summary>
public sealed class BuildingOperatorGrantCandidateQuery_Tests
{
    private static readonly DateTime At =
        new(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GRANTQUERY01_Active_Current_User_Sees_Only_Its_Building()
    {
        using var store = await DesignStore.CreateAsync();
        var actor = Guid.NewGuid();
        var other = Guid.NewGuid();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        await AddBuildingAsync(store, mine);
        await AddBuildingAsync(store, theirs);
        await AddGrantAsync(store, actor, mine, At.AddDays(-1), null);
        await AddGrantAsync(store, other, theirs, At.AddDays(-1), null);

        var allowed = await ReadAsync(store, actor, At);
        Assert.Equal(new[] { mine }, allowed);
    }

    [Fact]
    public async Task GRANTQUERY02_Future_Grant_Is_Not_Visible()
    {
        using var store = await DesignStore.CreateAsync();
        var actor = Guid.NewGuid();
        var building = Guid.NewGuid();
        await AddBuildingAsync(store, building);
        await AddGrantAsync(store, actor, building, At.AddSeconds(1), null);

        Assert.Empty(await ReadAsync(store, actor, At));
        Assert.Equal(new[] { building },
            await ReadAsync(store, actor, At.AddSeconds(1)));
    }

    [Fact]
    public async Task GRANTQUERY03_Expiration_Is_Exclusive()
    {
        using var store = await DesignStore.CreateAsync();
        var actor = Guid.NewGuid();
        var building = Guid.NewGuid();
        await AddBuildingAsync(store, building);
        await AddGrantAsync(store, actor, building, At, At.AddDays(1));

        Assert.Equal(new[] { building },
            await ReadAsync(store, actor, At.AddHours(23)));
        Assert.Empty(await ReadAsync(store, actor, At.AddDays(1)));
    }

    [Fact]
    public async Task GRANTQUERY04_Revoked_Row_Is_Excluded()
    {
        using var store = await DesignStore.CreateAsync();
        var actor = Guid.NewGuid();
        var building = Guid.NewGuid();
        await AddBuildingAsync(store, building);
        var id = await AddGrantAsync(store, actor, building, At, null);

        await using (var db = store.Open())
        {
            var grant = await db.Grants.SingleAsync(x => x.Id == id);
            grant.Revoke(Guid.NewGuid(), At.AddHours(1));
            await db.SaveChangesAsync();
        }

        Assert.Empty(await ReadAsync(store, actor, At.AddHours(2)));
    }

    [Fact]
    public async Task GRANTQUERY05_Soft_Deleted_Building_Is_Excluded()
    {
        using var store = await DesignStore.CreateAsync();
        var actor = Guid.NewGuid();
        var buildingId = Guid.NewGuid();
        await AddBuildingAsync(store, buildingId);
        await AddGrantAsync(store, actor, buildingId, At, null);

        await using (var db = store.Open())
        {
            var building = await db.Buildings.SingleAsync(x => x.Id == buildingId);
            db.Entry(building).Property(nameof(Building.IsDeleted))
                .CurrentValue = true;
            await db.SaveChangesAsync();
        }

        Assert.Empty(await ReadAsync(store, actor, At.AddDays(1)));
    }

    [Fact]
    public async Task GRANTQUERY06_Another_User_Does_Not_Inherit_A_Grant()
    {
        using var store = await DesignStore.CreateAsync();
        var building = Guid.NewGuid();
        await AddBuildingAsync(store, building);
        await AddGrantAsync(store, Guid.NewGuid(), building, At, null);

        Assert.Empty(await ReadAsync(store, Guid.NewGuid(), At));
    }

    [Fact]
    public async Task GRANTQUERY07_Expired_Grant_Cannot_Hide_Another_Active_Scope()
    {
        using var store = await DesignStore.CreateAsync();
        var actor = Guid.NewGuid();
        var expired = Guid.NewGuid();
        var active = Guid.NewGuid();
        await AddBuildingAsync(store, expired);
        await AddBuildingAsync(store, active);
        await AddGrantAsync(store, actor, expired, At, At.AddDays(1));
        await AddGrantAsync(store, actor, active, At, null);

        Assert.Equal(new[] { active },
            await ReadAsync(store, actor, At.AddDays(2)));
    }

    [Fact]
    public async Task GRANTQUERY08_Empty_Actor_Denies_And_NonUtc_Time_Throws()
    {
        using var store = await DesignStore.CreateAsync();
        await using var db = store.Open();

        Assert.Empty(await BuildingOperatorGrantCandidateQuery
            .FindActiveBuildingIdsAsync(db.Grants, db.Buildings, Guid.Empty, At));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await BuildingOperatorGrantCandidateQuery.FindActiveBuildingIdsAsync(
                db.Grants, db.Buildings, Guid.NewGuid(),
                DateTime.SpecifyKind(At, DateTimeKind.Unspecified)));
    }

    [Fact]
    public async Task GRANTQUERY09_Revocation_Is_Observed_On_Fresh_Select()
    {
        using var store = await DesignStore.CreateAsync();
        var actor = Guid.NewGuid();
        var building = Guid.NewGuid();
        await AddBuildingAsync(store, building);
        var id = await AddGrantAsync(store, actor, building, At, null);

        Assert.Equal(new[] { building },
            await ReadAsync(store, actor, At.AddMinutes(1)));

        await using (var db = store.Open())
        {
            var row = await db.Grants.SingleAsync(x => x.Id == id);
            row.Revoke(Guid.NewGuid(), At.AddMinutes(2));
            await db.SaveChangesAsync();
        }

        Assert.Empty(await ReadAsync(store, actor, At.AddMinutes(3)));
    }

    private static async Task<Guid[]> ReadAsync(
        DesignStore store, Guid user, DateTime now)
    {
        await using var db = store.Open();
        var result = await BuildingOperatorGrantCandidateQuery
            .FindActiveBuildingIdsAsync(db.Grants, db.Buildings, user, now);
        return result.ToArray();
    }

    private static async Task AddBuildingAsync(
        DesignStore store, Guid id)
    {
        await using var db = store.Open();
        db.Buildings.Add(new Building(id, "Membership query fixture",
            "Disposable SQLite only"));
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> AddGrantAsync(
        DesignStore store, Guid actor, Guid building,
        DateTime issuedAt, DateTime? expiresAt)
    {
        var id = Guid.NewGuid();
        await using var db = store.Open();
        db.Grants.Add(BuildingOperatorAccessGrant.Issue(
            id, actor, building, Guid.NewGuid(), issuedAt, expiresAt));
        await db.SaveChangesAsync();
        return id;
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
