using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using Volo.Abp.Security.Claims;
using Volo.Abp.Users;
using System.Threading.Tasks;
using BuildingManagement.Buildings;
using BuildingManagement.Security;
using Volo.Abp.Authorization;
using Volo.Abp.Data;
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

    [Fact]
    public async Task EFMEMBER07_Write_Membership_Lock_Precedes_Revoke_And_PostRevoke_Denies()
    {
        await InScratchAsync(async cs =>
        {
            var owner = Guid.NewGuid();
            var worker = Guid.NewGuid();
            Guid buildingId;
            await using (var seed = OpenGrantDb(cs))
            {
                await seed.Database.MigrateAsync();
                buildingId = (await new EfBuildingOwnerBootstrapAtomicWriter(seed)
                    .CreateWithOwnerAtomicallyAsync(owner, "Before", "Scratch", null)).Id;
                await new EfBuildingOperatorGrantAtomicMutationWriter(
                    seed, TimeProvider.System)
                    .AssignAtomicallyAsync(owner, worker, buildingId, null);
            }

            var locked = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var write = Task.Run(async () =>
            {
                await using var db = OpenGrantDb(cs);
                var fence = new EfBuildingScopedWriteFenceCandidate(
                    db, TimeProvider.System);
                return await fence.ExecuteAsync(worker, buildingId, async () =>
                {
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE \"Buildings\" SET \"Name\" = {"Completed"} WHERE \"Id\" = {buildingId}");
                    locked.SetResult(true);
                    await resume.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    return 1;
                });
            });

            try
            {
                await locked.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var start = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var revoke = Task.Run(async () =>
                {
                    await using var db = OpenGrantDb(cs);
                    var writer = new EfBuildingOperatorGrantAtomicMutationWriter(
                        db, TimeProvider.System);
                    start.SetResult(true);
                    await writer.RevokeAtomicallyAsync(
                        owner, worker, buildingId, 1);
                });
                await start.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Task.Delay(250);
                Assert.False(revoke.IsCompleted,
                    "Revocation must wait for write's shared grant-row lock.");
                resume.TrySetResult(true);
                Assert.Equal(1, await write.WaitAsync(TimeSpan.FromSeconds(15)));
                await revoke.WaitAsync(TimeSpan.FromSeconds(15));

                await using var verify = OpenGrantDb(cs);
                Assert.Equal("Completed",
                    (await verify.Buildings.SingleAsync(x => x.Id == buildingId)).Name);
                var fence = new EfBuildingScopedWriteFenceCandidate(
                    verify, TimeProvider.System);
                var touched = false;
                await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                    fence.ExecuteAsync(worker, buildingId, () =>
                    {
                        touched = true;
                        return Task.FromResult(2);
                    }));
                Assert.False(touched);
            }
            finally
            {
                resume.TrySetResult(true);
            }
        });
    }

    [Fact]
    public async Task EFMEMBER08_Fence_Cannot_Mutate_Another_Building()
    {
        await InScratchAsync(async cs =>
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            Guid buildingA, buildingB;
            await using (var seed = OpenGrantDb(cs))
            {
                await seed.Database.MigrateAsync();
                var bootstrap = new EfBuildingOwnerBootstrapAtomicWriter(seed);
                buildingA = (await bootstrap.CreateWithOwnerAtomicallyAsync(
                    a, "A", "Scratch", null)).Id;
                buildingB = (await bootstrap.CreateWithOwnerAtomicallyAsync(
                    b, "B", "Scratch", null)).Id;
            }
            await using var db = OpenGrantDb(cs);
            var fence = new EfBuildingScopedWriteFenceCandidate(
                db, TimeProvider.System);
            var touched = 0;
            Task<int> Update() => Task.FromResult(++touched);
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                fence.ExecuteAsync(a, buildingB, Update));
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                fence.ExecuteAsync(Guid.NewGuid(), buildingA, Update));
            Assert.Equal(0, touched);
            Assert.Equal(1, await fence.ExecuteAsync(a, buildingA, Update));
        });
    }

    [Fact]
    public async Task EFMEMBER09_Actual_Building_Write_Boundary_Uses_Authenticated_Server_Identity()
    {
        await InScratchAsync(async cs =>
        {
            var issuer = Guid.NewGuid();
            var other = Guid.NewGuid();
            Guid buildingA, buildingB;
            await using (var seed = OpenGrantDb(cs))
            {
                await seed.Database.MigrateAsync();
                var firstOwner = new EfBuildingOwnerBootstrapAtomicWriter(seed);
                buildingA = (await firstOwner.CreateWithOwnerAtomicallyAsync(
                    issuer, "Before", "Staging-only", null)).Id;
                buildingB = (await firstOwner.CreateWithOwnerAtomicallyAsync(
                    other, "Not allowed", "Staging-only", null)).Id;
            }

            var accessor = new ThreadCurrentPrincipalAccessor();
            await using var db = OpenGrantDb(cs);
            var principal = new CurrentUser(accessor);
            var adapter = new EfBuildingScopedWriteBoundaryCandidate(
                db, principal, TimeProvider.System);
            var touched = 0;
            Task<int> Mutate() => Task.FromResult(++touched);

            // Anonymous callers never enter the callback.
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                adapter.ExecuteBuildingAsync(buildingA, Mutate));
            Assert.Equal(0, touched);

            using (accessor.Change(new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(AbpClaimTypes.UserId, issuer.ToString("D")) },
                "IsolatedPGPrincipal"))))
            {
                await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                    adapter.ExecuteBuildingAsync(buildingB, Mutate));
                Assert.Equal(0, touched);
                var result = await adapter.ExecuteBuildingAsync(buildingA, async () =>
                {
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE \"Buildings\" SET \"Name\" = {"Committed via boundary"} WHERE \"Id\" = {buildingA}");
                    return 7;
                });
                Assert.Equal(7, result);
            }

            await using var verify = OpenGrantDb(cs);
            Assert.Equal("Committed via boundary",
                (await verify.Buildings.SingleAsync(x => x.Id == buildingA)).Name);

            // After the owner membership is revoked, the same server
            // principal can no longer enter its Building mutation.
            await using (var revoke = OpenGrantDb(cs))
            {
                // No self-revocation via public delegation API. This direct
                // scratch-only SQL simulates a trusted recovery action.
                await revoke.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"BuildingManagementOperatorBuildingGrants\" SET \"RevokedByUserId\" = {other}, \"RevokedAtUtc\" = {DateTime.UtcNow}, \"RevisionNumber\" = {2} WHERE \"BuildingId\" = {buildingA} AND \"OperatorUserId\" = {issuer}");
            }
            using (accessor.Change(new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(AbpClaimTypes.UserId, issuer.ToString("D")) },
                "IsolatedPGPrincipal"))))
            {
                await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                    adapter.ExecuteBuildingAsync(buildingA, Mutate));
                Assert.Equal(0, touched);
            }
        });
    }

    [Fact]
    public async Task EFMEMBER10_Existing_AbpStyle_UnitOfWork_Transaction_Retains_Lock_And_Commit_Ownership()
    {
        await InScratchAsync(async cs =>
        {
            var actor = Guid.NewGuid();
            Guid building;
            await using (var seed = OpenGrantDb(cs))
            {
                await seed.Database.MigrateAsync();
                building = (await new EfBuildingOwnerBootstrapAtomicWriter(seed)
                    .CreateWithOwnerAtomicallyAsync(
                        actor, "Before", "PG scratch", null)).Id;
            }

            // Mimic ABP UoW: transaction is established BEFORE the scoped
            // boundary runs. The boundary must NOT commit or rollback it.
            await using (var scope = OpenGrantDb(cs))
            {
                await using var outer = await scope.Database
                    .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                var fence = new EfBuildingScopedWriteFenceCandidate(
                    scope, TimeProvider.System);
                Assert.Equal(77, await fence.ExecuteAsync(actor, building, async () =>
                {
                    await scope.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE \"Buildings\" SET \"Name\" = {"Not committed"} WHERE \"Id\" = {building}");
                    return 77;
                }));
                Assert.NotNull(scope.Database.CurrentTransaction);
                await using (var independent = OpenGrantDb(cs))
                    Assert.Equal("Before",
                        (await independent.Buildings.SingleAsync(x =>
                            x.Id == building)).Name);
                // An outer UoW rollback MUST undo the successful callback.
                await outer.RollbackAsync();
            }

            await using (var verify = OpenGrantDb(cs))
                Assert.Equal("Before",
                    (await verify.Buildings.SingleAsync(x =>
                        x.Id == building)).Name);

            await using (var scope = OpenGrantDb(cs))
            {
                await using var outer = await scope.Database
                    .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                var fence = new EfBuildingScopedWriteFenceCandidate(
                    scope, TimeProvider.System);
                await fence.ExecuteAsync(actor, building, async () =>
                {
                    await scope.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE \"Buildings\" SET \"Name\" = {"Committed"} WHERE \"Id\" = {building}");
                    return true;
                });
                await outer.CommitAsync();
            }
            await using var final = OpenGrantDb(cs);
            Assert.Equal("Committed",
                (await final.Buildings.SingleAsync(x => x.Id == building)).Name);
        });
    }

    [Fact]
    public async Task EFMEMBER11_Unsupported_Ambient_Isolation_Fails_Closed_Without_Callback()
    {
        await InScratchAsync(async cs =>
        {
            var actor = Guid.NewGuid();
            Guid building;
            await using (var seed = OpenGrantDb(cs))
            {
                await seed.Database.MigrateAsync();
                building = (await new EfBuildingOwnerBootstrapAtomicWriter(seed)
                    .CreateWithOwnerAtomicallyAsync(
                        actor, "Before", "PG scratch", null)).Id;
            }
            await using var scope = OpenGrantDb(cs);
            await using var outer = await scope.Database
                .BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
            var fence = new EfBuildingScopedWriteFenceCandidate(
                scope, TimeProvider.System);
            var reached = false;
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                fence.ExecuteAsync(actor, building, () =>
                {
                    reached = true;
                    return Task.FromResult(true);
                }));
            Assert.False(reached);
            Assert.NotNull(scope.Database.CurrentTransaction);
            await outer.RollbackAsync();
        });
    }

    [Fact]
    public async Task EFMEMBER12_First_Owner_Bootstrap_Respects_Outer_UoW_Rollback()
    {
        await InScratchAsync(async cs =>
        {
            var root = Guid.NewGuid();
            Guid created;
            await using (var setup = OpenGrantDb(cs))
                await setup.Database.MigrateAsync();

            await using (var scoped = OpenGrantDb(cs))
            {
                await using var uow = await scoped.Database
                    .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                var createdBuilding = await new EfBuildingOwnerBootstrapAtomicWriter(scoped)
                    .CreateWithOwnerAtomicallyAsync(root, "Not committed", "PG", null);
                created = createdBuilding.Id;
                // Writer MUST leave the externally-owned transaction open.
                Assert.NotNull(scoped.Database.CurrentTransaction);
                await using (var outside = OpenGrantDb(cs))
                {
                    Assert.False(await outside.Buildings.AnyAsync(x => x.Id == created));
                    Assert.False(await outside.Grants.AnyAsync(x => x.BuildingId == created));
                }
                await uow.RollbackAsync();
            }

            await using var verify = OpenGrantDb(cs);
            Assert.False(await verify.Buildings.AnyAsync(x => x.Id == created));
            Assert.False(await verify.Grants.AnyAsync(x => x.BuildingId == created));
        });
    }

    [Fact]
    public async Task EFMEMBER13_Grant_And_Revoke_Respect_External_UoW_Commit_And_Rollback()
    {
        await InScratchAsync(async cs =>
        {
            var owner = Guid.NewGuid();
            var staff = Guid.NewGuid();
            Guid building;
            await using (var seed = OpenGrantDb(cs))
            {
                await seed.Database.MigrateAsync();
                building = (await new EfBuildingOwnerBootstrapAtomicWriter(seed)
                    .CreateWithOwnerAtomicallyAsync(owner, "A", "PG", null)).Id;
            }

            await using (var scoped = OpenGrantDb(cs))
            {
                await using var outer = await scoped.Database
                    .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await new EfBuildingOperatorGrantAtomicMutationWriter(
                    scoped, TimeProvider.System)
                    .AssignAtomicallyAsync(owner, staff, building, null);
                await using (var outside = OpenGrantDb(cs))
                    Assert.False(await outside.Grants.AnyAsync(x =>
                        x.OperatorUserId == staff));
                await outer.RollbackAsync();
            }
            await using (var verify = OpenGrantDb(cs))
                Assert.False(await verify.Grants.AnyAsync(x =>
                    x.OperatorUserId == staff));

            await using (var scoped = OpenGrantDb(cs))
            {
                await using var outer = await scoped.Database
                    .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await new EfBuildingOperatorGrantAtomicMutationWriter(
                    scoped, TimeProvider.System)
                    .AssignAtomicallyAsync(owner, staff, building, null);
                await outer.CommitAsync();
            }
            await using (var verify = OpenGrantDb(cs))
                Assert.Equal(1, await verify.Grants.CountAsync(x =>
                    x.OperatorUserId == staff && x.RevokedAtUtc == null));

            await using (var scoped = OpenGrantDb(cs))
            {
                await using var outer = await scoped.Database
                    .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await new EfBuildingOperatorGrantAtomicMutationWriter(
                    scoped, TimeProvider.System)
                    .RevokeAtomicallyAsync(owner, staff, building, 1);
                await outer.RollbackAsync();
            }
            await using (var verify = OpenGrantDb(cs))
                Assert.Equal(1, await verify.Grants.CountAsync(x =>
                    x.OperatorUserId == staff && x.RevokedAtUtc == null));

            await using (var scoped = OpenGrantDb(cs))
            {
                await using var outer = await scoped.Database
                    .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await new EfBuildingOperatorGrantAtomicMutationWriter(
                    scoped, TimeProvider.System)
                    .RevokeAtomicallyAsync(owner, staff, building, 1);
                await outer.CommitAsync();
            }
            await using var history = OpenGrantDb(cs);
            Assert.Equal(1, await history.Grants.CountAsync(x =>
                x.OperatorUserId == staff && x.RevokedAtUtc != null &&
                x.RevokedByUserId == owner && x.RevisionNumber == 2));
        });
    }

    [Fact]
    public async Task EFMEMBER14_Delegated_Manager_Cannot_Revoke_First_Owner_Grant()
    {
        await InScratchAsync(async cs =>
        {
            var founder = Guid.NewGuid();
            var manager = Guid.NewGuid();
            Guid building;
            await using (var seed = OpenGrantDb(cs))
            {
                await seed.Database.MigrateAsync();
                building = (await new EfBuildingOwnerBootstrapAtomicWriter(seed)
                    .CreateWithOwnerAtomicallyAsync(
                        founder, "Cannot orphan", "Scratch PG", null)).Id;
                await new EfBuildingOperatorGrantAtomicMutationWriter(
                    seed, TimeProvider.System)
                    .AssignAtomicallyAsync(founder, manager, building, null);
            }
            await using (var attempted = OpenGrantDb(cs))
            {
                await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                    new EfBuildingOperatorGrantAtomicMutationWriter(
                        attempted, TimeProvider.System)
                        .RevokeAtomicallyAsync(manager, founder, building, 1));
            }
            await using var verify = OpenGrantDb(cs);
            Assert.Equal(1, await verify.Grants.CountAsync(g =>
                g.OperatorUserId == founder && g.BuildingId == building &&
                g.RevokedAtUtc == null));
            Assert.Equal(1, await verify.Grants.CountAsync(g =>
                g.OperatorUserId == manager && g.BuildingId == building &&
                g.RevokedAtUtc == null));
        });
    }

    [Fact]
    public async Task EFMEMBER15_Revocation_Committed_First_Blocks_Delegation_In_Ambient_ReadCommitted_Uow()
    {
        await InScratchAsync(async cs =>
        {
            var founder = Guid.NewGuid();
            var manager = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            Guid building;
            await using (var seed = OpenGrantDb(cs))
            {
                await seed.Database.MigrateAsync();
                building = (await new EfBuildingOwnerBootstrapAtomicWriter(seed)
                    .CreateWithOwnerAtomicallyAsync(
                        founder, "First revoke", "Scratch PG", null)).Id;
                await new EfBuildingOperatorGrantAtomicMutationWriter(
                    seed, TimeProvider.System)
                    .AssignAtomicallyAsync(founder, manager, building, null);
            }

            await using var revokeDb = OpenGrantDb(cs);
            await using var revokeUow = await revokeDb.Database
                .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
            await new EfBuildingOperatorGrantAtomicMutationWriter(
                revokeDb, TimeProvider.System)
                .RevokeAtomicallyAsync(founder, manager, building, 1);

            var started = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var delegation = Task.Run(async () =>
            {
                await using var attempt = OpenGrantDb(cs);
                await using var uow = await attempt.Database
                    .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                started.TrySetResult(true);
                await new EfBuildingOperatorGrantAtomicMutationWriter(
                    attempt, TimeProvider.System)
                    .AssignAtomicallyAsync(manager, recipient, building, null);
                await uow.CommitAsync();
            });

            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Task.Delay(250);
                Assert.False(delegation.IsCompleted,
                    "Delegation must wait on the issuer membership being revoked.");
            }
            finally
            {
                await revokeUow.CommitAsync();
            }

            await Assert.ThrowsAsync<AbpAuthorizationException>(
                async () => await delegation.WaitAsync(TimeSpan.FromSeconds(15)));

            await using var verify = OpenGrantDb(cs);
            Assert.False(await verify.Grants.AnyAsync(g =>
                g.OperatorUserId == recipient && g.BuildingId == building));
            Assert.Equal(1, await verify.Grants.CountAsync(g =>
                g.OperatorUserId == manager && g.BuildingId == building &&
                g.RevokedAtUtc != null));
        });
    }

    [Fact]
    public async Task EFMEMBER16_Delegation_Locked_First_Must_Commit_Before_Issuer_Revocation()
    {
        await InScratchAsync(async cs =>
        {
            var founder = Guid.NewGuid();
            var manager = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            Guid building;
            await using (var seed = OpenGrantDb(cs))
            {
                await seed.Database.MigrateAsync();
                building = (await new EfBuildingOwnerBootstrapAtomicWriter(seed)
                    .CreateWithOwnerAtomicallyAsync(
                        founder, "First delegate", "Scratch PG", null)).Id;
                await new EfBuildingOperatorGrantAtomicMutationWriter(
                    seed, TimeProvider.System)
                    .AssignAtomicallyAsync(founder, manager, building, null);
            }

            var delegated = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var delegation = Task.Run(async () =>
            {
                await using var scope = OpenGrantDb(cs);
                await using var uow = await scope.Database
                    .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await new EfBuildingOperatorGrantAtomicMutationWriter(
                    scope, TimeProvider.System)
                    .AssignAtomicallyAsync(manager, recipient, building, null);
                delegated.TrySetResult(true);
                await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await uow.CommitAsync();
            });

            try
            {
                await delegated.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await using (var uncommitted = OpenGrantDb(cs))
                    Assert.False(await uncommitted.Grants.AnyAsync(g =>
                        g.OperatorUserId == recipient && g.BuildingId == building));

                var started = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var revocation = Task.Run(async () =>
                {
                    await using var scope = OpenGrantDb(cs);
                    await using var uow = await scope.Database
                        .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                    started.TrySetResult(true);
                    await new EfBuildingOperatorGrantAtomicMutationWriter(
                        scope, TimeProvider.System)
                        .RevokeAtomicallyAsync(founder, manager, building, 1);
                    await uow.CommitAsync();
                });
                await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Task.Delay(250);
                Assert.False(revocation.IsCompleted,
                    "Revocation must wait until delegated grant commits or rolls back.");
                release.TrySetResult(true);
                await delegation.WaitAsync(TimeSpan.FromSeconds(15));
                await revocation.WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
                release.TrySetResult(true);
            }

            await using var verify = OpenGrantDb(cs);
            Assert.Equal(1, await verify.Grants.CountAsync(g =>
                g.OperatorUserId == recipient && g.BuildingId == building &&
                g.RevokedAtUtc == null));
            Assert.Equal(1, await verify.Grants.CountAsync(g =>
                g.OperatorUserId == manager && g.BuildingId == building &&
                g.RevokedAtUtc != null));

            var laterActor = Guid.NewGuid();
            await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                new EfBuildingOperatorGrantAtomicMutationWriter(
                    verify, TimeProvider.System)
                    .AssignAtomicallyAsync(manager, laterActor, building, null));
        });
    }

    [Fact]
    public async Task EFMEMBER18_Recovery_Cosignature_Is_Atomic_And_Does_Not_Remove_Founding_Owner()
    {
        await InScratchAsync(async cs =>
        {
            var founder = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var evidence = Guid.NewGuid();
            Guid building;
            await using (var seed = OpenGrantDb(cs))
            {
                await seed.Database.MigrateAsync();
                building = (await new EfBuildingOwnerBootstrapAtomicWriter(seed)
                    .CreateWithOwnerAtomicallyAsync(
                        founder, "Founding grant intact", "Scratch PG", null)).Id;
                // Same-table shape as the additive runtime approval migration.
                // The dedicated full-chain test exercises the real migration.
                await seed.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE "BuildingManagementOwnerRecoverySignoffs" (
                        "EvidenceId" uuid NOT NULL,
                        "BuildingId" uuid NOT NULL,
                        "FoundingOwnerId" uuid NOT NULL,
                        "NewOwnerId" uuid NOT NULL,
                        "ApprovedByUserId" uuid NOT NULL,
                        "ApprovedAtUtc" timestamptz NOT NULL,
                        "ExpiresAtUtc" timestamptz NOT NULL,
                        "ConsumedAtUtc" timestamptz NULL,
                        "RecoveryGrantId" uuid NULL,
                        PRIMARY KEY ("EvidenceId", "ApprovedByUserId")
                    );
                    """);
                foreach (var signer in new[] { first, second })
                    await seed.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO "BuildingManagementOwnerRecoverySignoffs"
                            ("EvidenceId","BuildingId","FoundingOwnerId",
                             "NewOwnerId","ApprovedByUserId","ApprovedAtUtc","ExpiresAtUtc")
                        VALUES ({evidence}, {building}, {founder},
                                {recipient}, {signer}, {DateTime.UtcNow.AddMinutes(-1)},
                                {DateTime.UtcNow.AddMinutes(20)})
                        """);
            }

            await using (var operation = OpenGrantDb(cs))
            {
                var writer = new EfBuildingOwnerRecoveryAtomicWriter(
                    operation, TimeProvider.System);
                await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                    writer.RecoverAtomicallyAsync(building, founder, recipient,
                        first, first, evidence));
                await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                    writer.RecoverAtomicallyAsync(Guid.NewGuid(), founder, recipient,
                        first, second, evidence));
                var created = await writer.RecoverAtomicallyAsync(
                    building, founder, recipient, first, second, evidence);
                Assert.NotEqual(Guid.Empty, created);
                await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                    writer.RecoverAtomicallyAsync(
                        building, founder, recipient, first, second,
                        evidence));
                await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                    writer.RecoverAtomicallyAsync(
                        building, founder, recipient, first, second,
                        Guid.NewGuid()));
            }

            await using var verify = OpenGrantDb(cs);
            Assert.Equal(2, await verify.Grants.CountAsync(x =>
                x.BuildingId == building && x.RevokedAtUtc == null));
            var original = await verify.Grants.SingleAsync(x =>
                x.OperatorUserId == founder);
            Assert.Equal(founder, original.GrantedByUserId);
            Assert.Null(original.RevokedAtUtc);
            var consumed = await verify.Database
                .SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM \"BuildingManagementOwnerRecoverySignoffs\" WHERE \"EvidenceId\" = {0} AND \"ConsumedAtUtc\" IS NOT NULL", evidence)
                .SingleAsync();
            Assert.Equal(2, consumed);
            var recovered = await verify.Grants.SingleAsync(x =>
                x.OperatorUserId == recipient);
            Assert.Equal(first, recovered.GrantedByUserId);
            Assert.Equal(evidence.ToString("D"),
                recovered.GetProperty<string>("OwnerRecoveryEvidenceId"));
            Assert.Equal(second.ToString("D"),
                recovered.GetProperty<string>("OwnerRecoverySecondRootId"));
            Assert.Equal(founder.ToString("D"),
                recovered.GetProperty<string>("OwnerRecoveryFoundingUserId"));
        });
    }

    [Fact]
    public async Task EFMEMBER19_Recovery_Rejects_Ambiguous_SelfIssued_Founders()
    {
        await InScratchAsync(async cs =>
        {
            var founding = Guid.NewGuid();
            var forgedFounder = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var evidence = Guid.NewGuid();
            Guid building;
            await using (var seed = OpenGrantDb(cs))
            {
                await seed.Database.MigrateAsync();
                building = (await new EfBuildingOwnerBootstrapAtomicWriter(seed)
                    .CreateWithOwnerAtomicallyAsync(
                        founding, "Ambiguous founders", "Scratch PG", null)).Id;

                // A privileged, out-of-band INSERT can supply another
                // self-issued historical grant; unique membership indexes
                // alone do NOT prove there is only one founding identity.
                await seed.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "BuildingManagementOperatorBuildingGrants"
                        ("Id","OperatorUserId","BuildingId","GrantedByUserId",
                         "GrantedAtUtc","RevisionNumber","ExtraProperties",
                         "ConcurrencyStamp")
                    VALUES ({Guid.NewGuid()}, {forgedFounder}, {building},
                            {forgedFounder}, {DateTime.UtcNow.AddMinutes(-2)},
                            {1}, {"{}"}, {Guid.NewGuid().ToString("N")})
                    """);
            }
            await using (var operation = OpenGrantDb(cs))
            {
                await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                    new EfBuildingOwnerRecoveryAtomicWriter(
                        operation, TimeProvider.System)
                        .RecoverAtomicallyAsync(building, founding,
                            recipient, first, second, evidence));
                // Even presenting the second self-issued user as founder
                // does not let the caller select the winning record.
                await Assert.ThrowsAsync<AbpAuthorizationException>(() =>
                    new EfBuildingOwnerRecoveryAtomicWriter(
                        operation, TimeProvider.System)
                        .RecoverAtomicallyAsync(building, forgedFounder,
                            recipient, first, second, evidence));
            }
            await using var check = OpenGrantDb(cs);
            Assert.Equal(2, await check.Grants.CountAsync(g =>
                g.BuildingId == building &&
                g.GrantedByUserId == g.OperatorUserId &&
                g.RevokedAtUtc == null));
            Assert.False(await check.Grants.AnyAsync(g =>
                g.BuildingId == building && g.OperatorUserId == recipient));
        });
    }

    [Fact]
    public async Task EFMEMBER20_Concurrent_Second_Founder_Insert_Fences_Recovery_Then_Denies()
    {
        await InScratchAsync(async cs =>
        {
            var founder = Guid.NewGuid();
            var otherSelfIssued = Guid.NewGuid();
            var recipient = Guid.NewGuid();
            var firstSigner = Guid.NewGuid();
            var secondSigner = Guid.NewGuid();
            var evidence = Guid.NewGuid();
            Guid building;
            await using (var seed = OpenGrantDb(cs))
            {
                await seed.Database.MigrateAsync();
                building = (await new EfBuildingOwnerBootstrapAtomicWriter(seed)
                    .CreateWithOwnerAtomicallyAsync(
                        founder, "Concurrent founders", "Scratch PG", null)).Id;
            }

            // The other self-issued grant is not visible yet, but its
            // referencing FK INSERT holds KEY SHARE on the parent Building.
            await using var competingDb = OpenGrantDb(cs);
            await using var competingTx = await competingDb.Database
                .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
            await competingDb.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "BuildingManagementOperatorBuildingGrants"
                    ("Id","OperatorUserId","BuildingId","GrantedByUserId",
                     "GrantedAtUtc","RevisionNumber","ExtraProperties",
                     "ConcurrencyStamp")
                VALUES ({Guid.NewGuid()}, {otherSelfIssued}, {building},
                        {otherSelfIssued}, {DateTime.UtcNow.AddMinutes(-1)},
                        {1}, {"{}"}, {Guid.NewGuid().ToString("N")})
                """);

            var started = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var recovery = Task.Run(async () =>
            {
                await using var db = OpenGrantDb(cs);
                // Same supported isolation as the real ABP Building UoW.
                await using var uow = await db.Database
                    .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                started.TrySetResult(true);
                var id = await new EfBuildingOwnerRecoveryAtomicWriter(
                    db, TimeProvider.System).RecoverAtomicallyAsync(
                        building, founder, recipient, firstSigner,
                        secondSigner, evidence);
                await uow.CommitAsync();
                return id;
            });
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Task.Delay(250);
                Assert.False(recovery.IsCompleted,
                    "Recovery must wait for FK-enforced concurrent grant INSERT.");
            }
            finally
            {
                await competingTx.CommitAsync();
            }

            // Once the competing grant becomes visible, both self-issued
            // identities are detected. Recovery must reject both, without
            // issuing a grant, even though the initial lookup began earlier.
            await Assert.ThrowsAsync<AbpAuthorizationException>(
                async () => await recovery.WaitAsync(TimeSpan.FromSeconds(15)));
            await using var verify = OpenGrantDb(cs);
            Assert.Equal(2, await verify.Grants.CountAsync(x =>
                x.BuildingId == building &&
                x.OperatorUserId == x.GrantedByUserId &&
                x.RevokedAtUtc == null));
            Assert.False(await verify.Grants.AnyAsync(x =>
                x.BuildingId == building && x.OperatorUserId == recipient));
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
