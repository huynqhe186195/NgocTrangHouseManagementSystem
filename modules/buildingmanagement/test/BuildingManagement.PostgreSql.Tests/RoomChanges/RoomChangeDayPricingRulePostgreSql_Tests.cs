using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Contracts;
using BuildingManagement.Migrations;
using BuildingManagement.RoomChanges;
using BuildingManagement.Rooms;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Actual SQL migration + original .NET domain fingerprint in a rolled-back
/// scratch schema. No user database migrations and no persistent rows.
/// </summary>
public sealed class RoomChangeDayPricingRulePostgreSql_Tests
{
    private static readonly string Up = Assert.Single(
        new Phase2HDayProrationRuleCandidates().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string Down = Assert.Single(
        new Phase2HDayProrationRuleCandidates().DownOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly DateTime Created =
        new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

    private const string StubSchema = """
        CREATE TABLE "BuildingManagementRooms"("Id" uuid PRIMARY KEY);
        CREATE TABLE "BuildingManagementContracts"(
          "Id" uuid PRIMARY KEY, "RoomId" uuid NOT NULL,
          "Status" integer NOT NULL, "MonthlyRent" numeric(18,0) NOT NULL,
          "StartDate" date NOT NULL, "EndDate" date NULL
        );
        CREATE TABLE "BuildingManagementRoomChangeRequests"(
          "Id" uuid PRIMARY KEY, "SourceContractId" uuid NOT NULL,
          "SourceRoomId" uuid NOT NULL, "TargetRoomId" uuid NOT NULL,
          "BuildingId" uuid NOT NULL, "RequestedDate" date NOT NULL,
          "Status" integer NOT NULL, "CreatedAtUtc" timestamptz NOT NULL
        );
        CREATE TABLE "BuildingManagementRoomChangeTenantRosterSnapshots"(
          "Id" uuid PRIMARY KEY, "RoomChangeRequestId" uuid NOT NULL,
          "SourceContractId" uuid NOT NULL, "SourceRoomId" uuid NOT NULL,
          "BuildingId" uuid NOT NULL, "RosterCount" integer NOT NULL
        );
        CREATE TABLE "BuildingManagementRoomChangeBusinessPolicies"(
          "Id" uuid PRIMARY KEY, "RequestId" uuid NOT NULL,
          "RosterSnapshotId" uuid NOT NULL, "SourceContractId" uuid NOT NULL,
          "BuildingId" uuid NOT NULL, "ContractTreatment" integer NOT NULL,
          "PricingTreatment" integer NOT NULL, "TenantTreatment" integer NOT NULL,
          "PricingPolicyReference" varchar(500) NOT NULL,
          "ProposedByUserId" uuid NOT NULL, "EvidenceSha256" varchar(64) NOT NULL
        );
        CREATE TABLE "BuildingManagementRoomChangeBusinessPolicyReviews"(
          "Id" uuid PRIMARY KEY, "PolicyId" uuid NOT NULL,
          "RequestId" uuid NOT NULL, "BuildingId" uuid NOT NULL,
          "ProposalSha256" varchar(64) NOT NULL,
          "Accepted" boolean NOT NULL,
          "ReviewedByUserId" uuid NOT NULL,
          "ReviewedAtUtc" timestamptz NOT NULL
        );
        """;

    private sealed record F(
        RoomChangeRequest Request, Contract Source,
        RoomChangeTenantRosterSnapshotRecord Roster,
        RoomChangeBusinessPolicyRecord Policy,
        RoomChangeBusinessPolicyReviewRecord Review,
        RoomChangeDayPricingRuleCandidate Rule);

    private static F Fixture(
        DateOnly? date = null,
        RoomChangeTransferDayChargeOwner owner =
            RoomChangeTransferDayChargeOwner.TargetRoom,
        RoomChangeRentRoundingRule rounding =
            RoomChangeRentRoundingRule.NearestVndAwayFromZero,
        decimal sourceMonthlyRentVnd = 3_000_000m,
        decimal targetMonthlyRentVnd = 4_000_000m)
    {
        var d = date ?? new DateOnly(2026, 11, 15);
        var source = new Contract(Guid.NewGuid(), "SRC-SQL",
            Guid.NewGuid(), new DateTime(2026, 10, 1), null,
            sourceMonthlyRentVnd);
        source.Activate();
        var req = RoomChangeRequest.Submit(Guid.NewGuid(),
            source.Id, source.RoomId, Guid.NewGuid(), Guid.NewGuid(), d,
            "Owner-reviewed, documented transfer day pricing", Guid.NewGuid(),
            Created, ContractStatus.Active, RoomStatus.Occupied, RoomStatus.Available);
        var roster = RoomChangeTenantRosterSnapshotRecord.Capture(Guid.NewGuid(),
            req, new[] { new RoomChangeRosterMember(
                Guid.NewGuid(), ContractTenantRole.PrimaryTenant) },
            Guid.NewGuid(), Created.AddSeconds(1));
        var policy = RoomChangeBusinessPolicyRecord.Propose(Guid.NewGuid(),
            req, roster, RoomChangeContractTreatment.NewContract,
            RoomChangePricingTreatment.AgreedDayProration,
            RoomChangeDepositTreatment.CarryAllWithNoDifference,
            RoomChangeTenantTreatment.AllCurrentTenants,
            "SIGNED-SOURCE-TO-TARGET-CONTRACT-2026",
            "SIGNED-TRANSFER-DAY-AND-ROUNDING-2026",
            "SIGNED-CASH-DEPOSIT-RULE-2026",
            "SIGNED-ALL-TENANTS-TRANSFER-2026",
            Guid.NewGuid(), Created.AddSeconds(2));
        var review = RoomChangeBusinessPolicyReviewRecord.Review(Guid.NewGuid(),
            policy, req, roster, Guid.NewGuid(), Created.AddSeconds(3),
            true, "ROOT-ACCEPTED-TRANSFER-PRICING-2026",
            "Reviewed source and target financial rule");
        var quote = RoomChangeDailyRentQuoteCalculator.Calculate(
            d, source.MonthlyRent, targetMonthlyRentVnd, owner, rounding,
            policy.PricingPolicyReference);
        var rule = RoomChangeDayPricingRuleCandidate.Prepare(Guid.NewGuid(),
            req, source, roster, policy, review, quote, Guid.NewGuid(),
            Created.AddSeconds(4));
        return new F(req, source, roster, policy, review, rule);
    }

    [Fact]
    public async Task TRFPG31_Valid_Domain_Day_Rule_Has_Identical_SQL_Digest()
    {
        await Scratch(IsolationLevel.Serializable, async (db, tx) =>
        {
            var original = Fixture();
            await Seed(db, tx, original);
            await Insert(db, tx, original.Rule);
            var digest = await StringScalar(db, tx, $"""
                SELECT "BM_CalculateRoomChangeDayPricingRuleSha"(r)
                FROM "BuildingManagementRoomChangeDayPricingRuleCandidates" r
                WHERE r."Id"='{original.Rule.Id:D}'
                """);
            Assert.Equal(original.Rule.EvidenceSha256, digest);

            // Exact 0.5 VND must round UP on each independently priced line.
            // These are persisted PostgreSQL rows, not a calculator preview.
            foreach (var transfer in new[] {
                new DateOnly(2027, 2, 15), // 14/28 + 14/28
                new DateOnly(2027, 4, 16)  // 15/30 + 15/30
            })
            {
                var half = Fixture(transfer,
                    sourceMonthlyRentVnd: 45m, targetMonthlyRentVnd: 45m);
                await Seed(db, tx, half);
                await Insert(db, tx, half.Rule);
                await using var check = new NpgsqlCommand("""
                    SELECT "ProposedSourceRentVnd",
                           "ProposedTargetRentVnd", "EvidenceSha256",
                           "BM_CalculateRoomChangeDayPricingRuleSha"(r)
                    FROM "BuildingManagementRoomChangeDayPricingRuleCandidates" r
                    WHERE r."Id"=@id
                    """, db, tx);
                check.Parameters.AddWithValue("id", half.Rule.Id);
                await using var saved = await check.ExecuteReaderAsync();
                Assert.True(await saved.ReadAsync());
                Assert.Equal(23m, saved.GetDecimal(0));
                Assert.Equal(23m, saved.GetDecimal(1));
                Assert.Equal(half.Rule.EvidenceSha256, saved.GetString(2));
                Assert.Equal(half.Rule.EvidenceSha256, saved.GetString(3));
                Assert.False(await saved.ReadAsync());
            }
            Assert.Equal(3L, await Scalar(db, tx,
                """SELECT count(*) FROM "BuildingManagementRoomChangeDayPricingRuleCandidates" """));
        });
    }

    [Fact]
    public async Task TRFPG32_Forged_Amount_Or_Signed_Reference_Cannot_Insert()
    {
        await Scratch(IsolationLevel.Serializable, async (db, tx) =>
        {
            var f = Fixture();
            await Seed(db, tx, f);
            await Denied(db, tx, () => Insert(db, tx, f.Rule,
                sourceCharge: 1m), "23514");
            // Reject tampering with either signed rent line independently.
            // A valid combined total cannot excuse a forged target line.
            await Denied(db, tx, () => Insert(db, tx, f.Rule,
                targetCharge: 1m), "23514");
            // Source D-1 and target D must partition the calendar month.
            await Denied(db, tx, () => Insert(db, tx, f.Rule,
                targetDays: f.Rule.TargetChargeableDays + 1), "23514");
            // Neither the old-room partition nor calendar denominator
            // can be independently rewritten by a direct SQL client.
            await Denied(db, tx, () => Insert(db, tx, f.Rule,
                sourceDays: f.Rule.SourceChargeableDays + 1), "23514");
            await Denied(db, tx, () => Insert(db, tx, f.Rule,
                calendarDays: f.Rule.CalendarDays + 1), "23514");
            await Denied(db, tx, () => Insert(db, tx, f.Rule,
                pricingRef: "FORGED-D01-D02-TARIFF-DOCUMENT-2026"), "23514");
            await Denied(db, tx, () => Insert(db, tx, f.Rule,
                hash: new string('0', 64)), "23514");
        });
    }

    [Fact]
    public async Task TRFPG33_Reviewed_Policy_Must_Match_Transfer_And_Be_Accepted()
    {
        await Scratch(IsolationLevel.Serializable, async (db, tx) =>
        {
            var f = Fixture();
            await Seed(db, tx, f);
            await Run(db, tx, $"""
                UPDATE "BuildingManagementRoomChangeBusinessPolicyReviews"
                 SET "Accepted"=FALSE WHERE "Id"='{f.Review.Id:D}'
                """);
            await Denied(db, tx, () => Insert(db, tx, f.Rule), "23514");
            await Run(db, tx, $"""
                UPDATE "BuildingManagementRoomChangeBusinessPolicyReviews"
                 SET "Accepted"=TRUE WHERE "Id"='{f.Review.Id:D}'
                """);
            await Run(db, tx, $"""
                UPDATE "BuildingManagementRoomChangeBusinessPolicies"
                 SET "PricingTreatment"=1 WHERE "Id"='{f.Policy.Id:D}'
                """);
            await Denied(db, tx, () => Insert(db, tx, f.Rule), "23514");
        });
    }

    [Fact]
    public async Task TRFPG34_Day_And_Leap_Month_Are_Validated_By_SQL()
    {
        // Preserve the historical negative SQL probes for unapproved
        // source-day/truncation candidates; signed D01/D02 execution uses
        // the TARGET day and whole-VND midpoint-away-from-zero instead.
        foreach (var date in new[] { new DateOnly(2028, 2, 29),
                    new DateOnly(2026, 12, 15) })
        {
            await Scratch(IsolationLevel.Serializable, async (db, tx) =>
            {
                var f = Fixture(date, RoomChangeTransferDayChargeOwner.SourceRoom,
                    RoomChangeRentRoundingRule.TruncateToWholeVnd);
                await Seed(db, tx, f);
                await Insert(db, tx, f.Rule);
                await Denied(db, tx, () => Insert(db, tx,
                    f.Rule, targetDays: f.Rule.TargetChargeableDays + 1), "23514");
            });
        }

        // 118 genuine, signed, reviewed and SQL-persisted candidate rows,
        // sharing one throwaway SERIALIZABLE scratch schema/transaction.
        // This tests source-day/target-day ownership and SQL rounding for
        // every date in real 28/29/30/31-day months. It does NOT issue
        // an invoice, commit an H4 transfer or settle a deposit.
        await Scratch(IsolationLevel.Serializable, async (db, tx) =>
        {
            var persisted = 0;
            foreach (var first in new[] {
                new DateOnly(2027, 2, 1), // nonleap: 28 days
                new DateOnly(2028, 2, 1), // leap: 29 days
                new DateOnly(2027, 4, 1), // 30 days
                new DateOnly(2027, 1, 1)  // 31 days
            })
            {
                var days = DateTime.DaysInMonth(first.Year, first.Month);
                for (var day = 1; day <= days; day++)
                {
                    var transfer = new DateOnly(first.Year, first.Month, day);
                    var f = Fixture(transfer);
                    await Seed(db, tx, f);
                    await Insert(db, tx, f.Rule);

                    // Independent oracle: each room is rounded separately.
                    // Never use the production calculator to form expected
                    // numeric amounts or the expected day partition.
                    var sourceDays = day - 1;
                    var targetDays = days - day + 1;
                    var sourceVnd = decimal.Round(
                        3_000_000m * sourceDays / days, 0,
                        MidpointRounding.AwayFromZero);
                    var targetVnd = decimal.Round(
                        4_000_000m * targetDays / days, 0,
                        MidpointRounding.AwayFromZero);

                    await using var sql = new NpgsqlCommand("""
                        SELECT "TransferDate", "TransferDayOwner", "Rounding",
                               "CalendarDays", "SourceChargeableDays",
                               "TargetChargeableDays", "SourceMonthlyRentVnd",
                               "TargetMonthlyRentVnd", "ProposedSourceRentVnd",
                               "ProposedTargetRentVnd", "EvidenceSha256",
                               "BM_CalculateRoomChangeDayPricingRuleSha"(r)
                        FROM "BuildingManagementRoomChangeDayPricingRuleCandidates" r
                        WHERE r."Id"=@id
                        """, db, tx);
                    sql.Parameters.AddWithValue("id", f.Rule.Id);
                    await using var saved = await sql.ExecuteReaderAsync();
                    Assert.True(await saved.ReadAsync());
                    Assert.Equal(transfer, saved.GetFieldValue<DateOnly>(0));
                    Assert.Equal((int)RoomChangeTransferDayChargeOwner.TargetRoom,
                        saved.GetInt32(1));
                    Assert.Equal((int)RoomChangeRentRoundingRule.NearestVndAwayFromZero,
                        saved.GetInt32(2));
                    Assert.Equal(days, saved.GetInt32(3));
                    Assert.Equal(sourceDays, saved.GetInt32(4));
                    Assert.Equal(targetDays, saved.GetInt32(5));
                    Assert.Equal(days, saved.GetInt32(4) + saved.GetInt32(5));
                    Assert.Equal(3_000_000m, saved.GetDecimal(6));
                    Assert.Equal(4_000_000m, saved.GetDecimal(7));
                    Assert.Equal(sourceVnd, saved.GetDecimal(8));
                    Assert.Equal(targetVnd, saved.GetDecimal(9));
                    Assert.Equal(f.Rule.EvidenceSha256, saved.GetString(10));
                    Assert.Equal(f.Rule.EvidenceSha256, saved.GetString(11));
                    Assert.False(await saved.ReadAsync());
                    persisted++;
                }
            }
            Assert.Equal(118, persisted);
            Assert.Equal(118L, await Scalar(db, tx,
                """SELECT count(*) FROM "BuildingManagementRoomChangeDayPricingRuleCandidates" """));
        });
    }

    [Fact]
    public async Task TRFPG35_Rule_Is_AppendOnly_Unique_And_DowngradeProtected()
    {
        await Scratch(IsolationLevel.Serializable, async (db, tx) =>
        {
            var f = Fixture();
            await Seed(db, tx, f);
            await Insert(db, tx, f.Rule);
            // Even a correctly hashed second rule for the same request
            // must not overwrite or coexist with the first reviewed decision.
            var quote = RoomChangeDailyRentQuoteCalculator.Calculate(
                f.Rule.TransferDate, f.Source.MonthlyRent,
                f.Rule.TargetMonthlyRentVnd, f.Rule.TransferDayOwner,
                f.Rule.Rounding, f.Policy.PricingPolicyReference);
            var replay = RoomChangeDayPricingRuleCandidate.Prepare(
                Guid.NewGuid(), f.Request, f.Source, f.Roster, f.Policy,
                f.Review, quote, Guid.NewGuid(), Created.AddSeconds(5));
            await Denied(db, tx, () => Insert(db, tx, replay), "23505");
            await Denied(db, tx, () => Run(db, tx, $"""
                UPDATE "BuildingManagementRoomChangeDayPricingRuleCandidates"
                 SET "EvidenceSha256"='{new string('a', 64)}'
                 WHERE "Id"='{f.Rule.Id:D}'
                """), "23514");
            await Denied(db, tx, () => Run(db, tx, $"""
                DELETE FROM "BuildingManagementRoomChangeDayPricingRuleCandidates"
                 WHERE "Id"='{f.Rule.Id:D}'
                """), "23514");
            await Denied(db, tx, () => Run(db, tx, Down), "23514");
            await Denied(db, tx, () => Run(db, tx,
                """TRUNCATE "BuildingManagementRoomChangeDayPricingRuleCandidates" """),
                "23514");
        });
    }

    [Fact]
    public async Task TRFPG36_Nonserializable_Attempt_Must_Fail()
    {
        await Scratch(IsolationLevel.ReadCommitted, async (db, tx) =>
        {
            var f = Fixture();
            await Seed(db, tx, f);
            await Denied(db, tx, () => Insert(db, tx, f.Rule), "23514");
        });
    }

    [Fact]
    public async Task TRFPG37_Empty_Rollback_And_Reapply_Are_Safe()
    {
        await Scratch(IsolationLevel.Serializable, async (db, tx) =>
        {
            await Run(db, tx, Down);
            await Run(db, tx, Up);
            var f = Fixture();
            await Seed(db, tx, f);
            await Insert(db, tx, f.Rule);
        });
    }

    private static async Task Seed(NpgsqlConnection db, NpgsqlTransaction tx, F f)
    {
        var r=f.Request; var s=f.Source; var p=f.Policy; var v=f.Review;
        var roster=f.Roster;
        await Run(db, tx, $"""
            INSERT INTO "BuildingManagementRooms" VALUES
             ('{r.SourceRoomId:D}'),('{r.TargetRoomId:D}');
            INSERT INTO "BuildingManagementContracts"
             VALUES ('{s.Id:D}','{s.RoomId:D}',2,{s.MonthlyRent},
                     '{s.StartDate:yyyy-MM-dd}',NULL);
            INSERT INTO "BuildingManagementRoomChangeRequests"
             VALUES ('{r.Id:D}','{r.SourceContractId:D}','{r.SourceRoomId:D}',
                     '{r.TargetRoomId:D}','{r.BuildingId:D}',
                     '{r.RequestedDate:yyyy-MM-dd}',0,'{r.CreatedAtUtc:O}');
            INSERT INTO "BuildingManagementRoomChangeTenantRosterSnapshots"
             VALUES ('{roster.Id:D}','{r.Id:D}','{s.Id:D}',
                     '{s.RoomId:D}','{r.BuildingId:D}',{roster.RosterCount});
            INSERT INTO "BuildingManagementRoomChangeBusinessPolicies"
             VALUES ('{p.Id:D}','{r.Id:D}','{roster.Id:D}','{s.Id:D}',
                     '{r.BuildingId:D}',1,2,1,'{p.PricingPolicyReference}',
                     '{p.ProposedByUserId:D}','{p.EvidenceSha256}');
            INSERT INTO "BuildingManagementRoomChangeBusinessPolicyReviews"
             VALUES ('{v.Id:D}','{p.Id:D}','{r.Id:D}','{r.BuildingId:D}',
                     '{v.ProposalSha256}',TRUE,'{v.ReviewedByUserId:D}',
                     '{v.ReviewedAtUtc:O}');
            """);
    }

    private static async Task Insert(NpgsqlConnection db, NpgsqlTransaction tx,
        RoomChangeDayPricingRuleCandidate r, decimal? sourceCharge = null,
        string? pricingRef = null, string? hash = null, int? targetDays = null,
        decimal? targetCharge = null, int? sourceDays = null,
        int? calendarDays = null)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO "BuildingManagementRoomChangeDayPricingRuleCandidates"
             ("Id","RequestId","SourceContractId","SourceRoomId","TargetRoomId",
              "BuildingId","RosterSnapshotId","PolicyId","PolicyReviewId",
              "TransferDate","TransferDayOwner","Rounding","CalendarDays",
              "SourceChargeableDays","TargetChargeableDays","SourceMonthlyRentVnd",
              "TargetMonthlyRentVnd","ProposedSourceRentVnd","ProposedTargetRentVnd",
              "SignedPricingRuleReference","PreparedByUserId","PreparedAtUtc",
              "EvidenceSha256")
            VALUES
             (@id,@req,@source,@from,@to,@building,@roster,@policy,@review,
              @date,@owner,@rounding,@days,@oldDays,@newDays,@oldRent,
              @newRent,@oldPrice,@newPrice,@rule,@actor,@time,@sha)
            """, db, tx);
        cmd.Parameters.AddWithValue("id",r.Id);
        cmd.Parameters.AddWithValue("req",r.RequestId);
        cmd.Parameters.AddWithValue("source",r.SourceContractId);
        cmd.Parameters.AddWithValue("from",r.SourceRoomId);
        cmd.Parameters.AddWithValue("to",r.TargetRoomId);
        cmd.Parameters.AddWithValue("building",r.BuildingId);
        cmd.Parameters.AddWithValue("roster",r.RosterSnapshotId);
        cmd.Parameters.AddWithValue("policy",r.PolicyId);
        cmd.Parameters.AddWithValue("review",r.PolicyReviewId);
        cmd.Parameters.AddWithValue("date",r.TransferDate);
        cmd.Parameters.AddWithValue("owner",(int)r.TransferDayOwner);
        cmd.Parameters.AddWithValue("rounding",(int)r.Rounding);
        cmd.Parameters.AddWithValue("days",calendarDays??r.CalendarDays);
        cmd.Parameters.AddWithValue("oldDays",sourceDays??r.SourceChargeableDays);
        cmd.Parameters.AddWithValue("newDays",targetDays??r.TargetChargeableDays);
        cmd.Parameters.AddWithValue("oldRent",r.SourceMonthlyRentVnd);
        cmd.Parameters.AddWithValue("newRent",r.TargetMonthlyRentVnd);
        cmd.Parameters.AddWithValue("oldPrice",sourceCharge??r.ProposedSourceRentVnd);
        cmd.Parameters.AddWithValue("newPrice",targetCharge??r.ProposedTargetRentVnd);
        cmd.Parameters.AddWithValue("rule",pricingRef??r.SignedPricingRuleReference);
        cmd.Parameters.AddWithValue("actor",r.PreparedByUserId);
        cmd.Parameters.AddWithValue("time",r.PreparedAtUtc);
        cmd.Parameters.AddWithValue("sha",hash??r.EvidenceSha256);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Scratch(IsolationLevel isolation,
        Func<NpgsqlConnection,NpgsqlTransaction,Task> test)
    {
        var conn=BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        await using var db=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(conn)
        { Pooling=false, CommandTimeout=45 }.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName,db.Database);
        var schema="bm_trf_day_rule_"+Guid.NewGuid().ToString("N");
        await using (var tx=await db.BeginTransactionAsync(isolation))
        {
            try
            {
                await Run(db,tx,$"""CREATE SCHEMA "{schema}" """);
                await Run(db,tx,$"""SET LOCAL search_path TO "{schema}" """);
                await Run(db,tx,StubSchema);
                await Run(db,tx,Up);
                await test(db,tx);
            }
            finally { await tx.RollbackAsync(); }
        }
        Assert.Equal(0L,await Scalar(db,null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task Denied(NpgsqlConnection db,
        NpgsqlTransaction tx,Func<Task> action,string expected)
    {
        await Run(db,tx,"SAVEPOINT trf_rule_probe");
        string? state=null;
        try { await action(); }
        catch(PostgresException e){state=e.SqlState;}
        finally
        {
            await Run(db,tx,"ROLLBACK TO SAVEPOINT trf_rule_probe");
            await Run(db,tx,"RELEASE SAVEPOINT trf_rule_probe");
        }
        Assert.Equal(expected,state);
    }

    private static async Task Run(NpgsqlConnection db,NpgsqlTransaction? tx,string sql)
    {
        await using var cmd=new NpgsqlCommand(sql,db,tx);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(NpgsqlConnection db,
        NpgsqlTransaction? tx,string sql)
    {
        await using var cmd=new NpgsqlCommand(sql,db,tx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<string?> StringScalar(
        NpgsqlConnection db,NpgsqlTransaction tx,string sql)
    {
        await using var cmd=new NpgsqlCommand(sql,db,tx);
        return (string?)await cmd.ExecuteScalarAsync();
    }
}
