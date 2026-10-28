using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
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
