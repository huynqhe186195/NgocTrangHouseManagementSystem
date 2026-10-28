using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Security;
using Volo.Abp.Authorization;
using BuildingManagement.EntityFrameworkCore.EntityConfigurations;
using BuildingManagement.EntityFrameworkCore.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

// A REAL EF Core migration, scoped exclusively to the disposable test
// assembly/DbContext. Never part of the production migration chain.
[DbContext(typeof(SliceAGrantScratchDb))]
[Migration("202610280001_SliceAGrantScratch")]
public sealed class SliceAGrantScratchMigration : Migration
{
    protected override void Up(MigrationBuilder builder)
        => builder.Sql(Sql("SliceA_OperatorBuildingGrant_Up_Candidate.sql"));

    protected override void Down(MigrationBuilder builder)
        => builder.Sql(Sql("SliceA_OperatorBuildingGrant_Down_Candidate.sql"));

    private static string Sql(string name)
    {
        using var stream = typeof(SliceAGrantScratchMigration)
            .Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Missing review SQL " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().TrimStart('\uFEFF');
    }
}

public sealed class SliceAGrantScratchDb : DbContext
{
    public SliceAGrantScratchDb(DbContextOptions<SliceAGrantScratchDb> options)
        : base(options) { }

    public DbSet<Building> Buildings => Set<Building>();
    public DbSet<BuildingOperatorAccessGrant> Grants
        => Set<BuildingOperatorAccessGrant>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.ApplyConfiguration(new BuildingConfiguration());
        model.ApplyConfiguration(new BuildingOperatorAccessGrantDesignConfiguration());
    }
}

public sealed class SliceABuildingPrerequisiteDb : DbContext
{
    public SliceABuildingPrerequisiteDb(
        DbContextOptions<SliceABuildingPrerequisiteDb> options)
        : base(options) { }

    public DbSet<Building> Buildings => Set<Building>();

    protected override void OnModelCreating(ModelBuilder model)
        => model.ApplyConfiguration(new BuildingConfiguration());
}

public sealed class SliceAGrantEfMigrationPostgreSql_Tests
{
    [Fact]
    public async Task EFMEMBER01_Up_Creates_Building_And_Owner_Atomically_Down_Rejects_History()
    {
        await InScratchAsync(async cs =>
        {
            await using var db = OpenGrantDb(cs);
            await db.Database.MigrateAsync();
            Assert.Contains("202610280001_SliceAGrantScratch",
                await db.Database.GetAppliedMigrationsAsync());

            var owner = Guid.NewGuid();
            var building = await new EfBuildingOwnerBootstrapAtomicWriter(db)
                .CreateWithOwnerAtomicallyAsync(owner, "A", "Scratch", null);

            await using (var verify = OpenGrantDb(cs))
            {
                var grant = await verify.Grants.AsNoTracking().SingleAsync();
                Assert.Equal(owner, grant.OperatorUserId);
                Assert.Equal(owner, grant.GrantedByUserId);
                Assert.Equal(building.Id, grant.BuildingId);
            }

            await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.MigrateAsync("0"));
            await using var after = OpenGrantDb(cs);
            Assert.Equal(1, await after.Buildings.CountAsync());
            Assert.Equal(1, await after.Grants.CountAsync());
        });
    }

    [Fact]
    public async Task EFMEMBER02_Grant_Insert_Fails_Entire_Bootstrap_And_Empty_Down_Works()
    {
        await InScratchAsync(async cs =>
        {
            await using var db = OpenGrantDb(cs);
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE FUNCTION fail_scratch_grant() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'forced grant error';
                END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER fail_scratch_grant
                BEFORE INSERT ON "BuildingManagementOperatorBuildingGrants"
                FOR EACH ROW EXECUTE FUNCTION fail_scratch_grant();
                """);

            await Assert.ThrowsAsync<DbUpdateException>(() =>
                new EfBuildingOwnerBootstrapAtomicWriter(db)
                    .CreateWithOwnerAtomicallyAsync(
                        Guid.NewGuid(), "Rollback", "Scratch", null));
            await using (var verify = OpenGrantDb(cs))
            {
                Assert.Equal(0, await verify.Buildings.CountAsync());
                Assert.Equal(0, await verify.Grants.CountAsync());
            }
            await db.Database.ExecuteSqlRawAsync(
                """
                DROP TRIGGER fail_scratch_grant ON
                    "BuildingManagementOperatorBuildingGrants";
                DROP FUNCTION fail_scratch_grant();
                """);
            await db.Database.MigrateAsync("0");
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
            await db.Database.MigrateAsync();
            Assert.Contains("202610280001_SliceAGrantScratch",
                await db.Database.GetAppliedMigrationsAsync());
        });
    }

    [Fact]
    public async Task EFMEMBER03_Two_Owners_Have_Disjoint_SQL_Membership()
    {
        await InScratchAsync(async cs =>
        {
            await using var db = OpenGrantDb(cs);
            await db.Database.MigrateAsync();
            var writer = new EfBuildingOwnerBootstrapAtomicWriter(db);
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var buildingA = await writer.CreateWithOwnerAtomicallyAsync(
                a, "A", "Scratch A", null);
            var buildingB = await writer.CreateWithOwnerAtomicallyAsync(
                b, "B", "Scratch B", null);
            await using var reader = OpenGrantDb(cs);
            var now = DateTime.UtcNow.AddMinutes(1);
            var grantsA = await BuildingOperatorGrantCandidateQuery
                .FindActiveBuildingIdsAsync(reader.Grants, reader.Buildings, a, now);
            var grantsB = await BuildingOperatorGrantCandidateQuery
                .FindActiveBuildingIdsAsync(reader.Grants, reader.Buildings, b, now);
            Assert.Equal(new[] { buildingA.Id }, grantsA);
            Assert.Equal(new[] { buildingB.Id }, grantsB);
        });
    }

    [Fact]
    public async Task EFMEMBER04_Only_Existing_Building_Owner_Can_Delegate_And_Never_To_Self()
    {
        await InScratchAsync(async cs =>
        {
            var issuerA = Guid.NewGuid();
            var issuerB = Guid.NewGuid();
            Guid buildingA, buildingB;
            await using (var seed = OpenGrantDb(cs))
            {
                await seed.Database.MigrateAsync();
                var bootstrap = new EfBuildingOwnerBootstrapAtomicWriter(seed);
                buildingA = (await bootstrap.CreateWithOwnerAtomicallyAsync(
                    issuerA, "A", "Scratch A", null)).Id;
                buildingB = (await bootstrap.CreateWithOwnerAtomicallyAsync(
                    issuerB, "B", "Scratch B", null)).Id;
            }
            var recipient = Guid.NewGuid();
            await using var db = OpenGrantDb(cs);
            var writer = new EfBuildingOperatorGrantAtomicMutationWriter(
                db, TimeProvider.System);
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                writer.AssignAtomicallyAsync(issuerA, issuerA, buildingA, null));
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                writer.AssignAtomicallyAsync(issuerA, recipient, buildingB, null));
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                writer.AssignAtomicallyAsync(Guid.NewGuid(), recipient, buildingA, null));
            var id = await writer.AssignAtomicallyAsync(
                issuerA, recipient, buildingA, null);
            await using var verify = OpenGrantDb(cs);
            var grant = await verify.Grants.AsNoTracking()
                .SingleAsync(x => x.Id == id);
            Assert.Equal(issuerA, grant.GrantedByUserId);
            Assert.Equal(buildingA, grant.BuildingId);
            Assert.False(await verify.Grants.AnyAsync(x =>
                x.BuildingId == buildingB && x.OperatorUserId == recipient));
        });
    }

    [Fact]
    public async Task EFMEMBER05_Revoke_With_Revision_Retains_History_And_Allows_Regrant()
    {
        await InScratchAsync(async cs =>
        {
            var issuer = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            Guid building;
            await using (var seed = OpenGrantDb(cs))
            {
                await seed.Database.MigrateAsync();
                building = (await new EfBuildingOwnerBootstrapAtomicWriter(seed)
                    .CreateWithOwnerAtomicallyAsync(issuer, "A", "Scratch", null)).Id;
            }
            Guid oldId;
            await using (var first = OpenGrantDb(cs))
            {
                var w = new EfBuildingOperatorGrantAtomicMutationWriter(
                    first, TimeProvider.System);
                oldId = await w.AssignAtomicallyAsync(issuer, recipient, building, null);
            }
            await using (var duplicate = OpenGrantDb(cs))
            {
                await Assert.ThrowsAsync<DbUpdateException>(() =>
                    new EfBuildingOperatorGrantAtomicMutationWriter(
                        duplicate, TimeProvider.System)
                        .AssignAtomicallyAsync(issuer, recipient, building, null));
            }
            await using (var revoke = OpenGrantDb(cs))
            {
                var w = new EfBuildingOperatorGrantAtomicMutationWriter(
                    revoke, TimeProvider.System);
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
                    w.RevokeAtomicallyAsync(issuer, recipient, building, 123));
                await w.RevokeAtomicallyAsync(issuer, recipient, building, 1);
            }
            await using (var regrant = OpenGrantDb(cs))
            {
                var next = await new EfBuildingOperatorGrantAtomicMutationWriter(
                    regrant, TimeProvider.System)
                    .AssignAtomicallyAsync(issuer, recipient, building, null);
                Assert.NotEqual(oldId, next);
            }
            await using var verify = OpenGrantDb(cs);
            Assert.Equal(2, await verify.Grants.CountAsync(x =>
                x.OperatorUserId == recipient && x.BuildingId == building));
            Assert.Equal(1, await verify.Grants.CountAsync(x =>
                x.OperatorUserId == recipient && x.BuildingId == building &&
                x.RevokedAtUtc == null));
            var history = await verify.Grants.SingleAsync(x => x.Id == oldId);
            Assert.Equal(2, history.RevisionNumber);
            Assert.Equal(issuer, history.RevokedByUserId);
        });
    }

    [Fact]
    public async Task EFMEMBER06_Concurrent_Assignment_Cannot_Create_Two_Active_Grants()
    {
        await InScratchAsync(async cs =>
        {
            var issuer = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            Guid building;
            await using (var seed = OpenGrantDb(cs))
            {
                await seed.Database.MigrateAsync();
                building = (await new EfBuildingOwnerBootstrapAtomicWriter(seed)
                    .CreateWithOwnerAtomicallyAsync(issuer, "A", "Scratch", null)).Id;
            }
            async Task<bool> TryAssignAsync()
            {
                await using var db = OpenGrantDb(cs);
                try
                {
                    await new EfBuildingOperatorGrantAtomicMutationWriter(
                        db, TimeProvider.System).AssignAtomicallyAsync(
                            issuer, recipient, building, null);
                    return true;
                }
                catch (DbUpdateException ex) when (
                    ex.InnerException is PostgresException pg &&
                    pg.SqlState is "40001" or "23505")
                {
                    return false;
                }
                catch (InvalidOperationException ex) when (
                    ex.InnerException is DbUpdateException inner &&
                    inner.InnerException is PostgresException pg &&
                    pg.SqlState == "40001")
                {
                    // Npgsql EF execution strategy wraps serialization
                    // failures as transient errors. Caller must rerun a
                    // fresh authorization+transaction, never auto-retry
                    // without checking account and permission again.
                    return false;
                }
                catch (PostgresException ex) when (
                    ex.SqlState is "40001" or "23505")
                {
                    return false;
                }
            }
            var results = await Task.WhenAll(TryAssignAsync(), TryAssignAsync());
            Assert.Equal(1, results.Count(x => x));
            await using var check = OpenGrantDb(cs);
            Assert.Equal(1, await check.Grants.CountAsync(x =>
                x.OperatorUserId == recipient && x.BuildingId == building &&
                x.RevokedAtUtc == null));
        });
    }

    private static SliceAGrantScratchDb OpenGrantDb(string cs)
        => new(new DbContextOptionsBuilder<SliceAGrantScratchDb>()
            .UseNpgsql(cs, n => n.MigrationsAssembly(
                typeof(SliceAGrantScratchMigration).Assembly.FullName))
            .Options);

    private static async Task InScratchAsync(Func<string, Task> action)
    {
        var cs = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        var options = new NpgsqlConnectionStringBuilder(cs)
        { Pooling = false, CommandTimeout = 20 };
        var schema = "bm_grant_ef_" + Guid.NewGuid().ToString("N");
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema,
                @"\Abm_grant_ef_[0-9a-f]{32}\z"))
            throw new InvalidOperationException("Unsafe scratch schema.");

        await using var admin = new NpgsqlConnection(options.ConnectionString);
        await admin.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,
            admin.Database);
        await using (var command = new NpgsqlCommand(
            $"CREATE SCHEMA \"{schema}\"", admin))
            await command.ExecuteNonQueryAsync();

        options.SearchPath = schema;
        try
        {
            await using (var prerequisite = new SliceABuildingPrerequisiteDb(
                new DbContextOptionsBuilder<SliceABuildingPrerequisiteDb>()
                    .UseNpgsql(options.ConnectionString).Options))
            {
                await prerequisite.Database.ExecuteSqlRawAsync(
                    prerequisite.Database.GenerateCreateScript());
            }
            await action(options.ConnectionString);
        }
        finally
        {
            await using var drop = new NpgsqlCommand(
                $"DROP SCHEMA \"{schema}\" CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
