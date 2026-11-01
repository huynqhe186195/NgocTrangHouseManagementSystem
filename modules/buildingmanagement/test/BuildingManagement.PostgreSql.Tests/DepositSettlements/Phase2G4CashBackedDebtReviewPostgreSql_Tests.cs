using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Real PostgreSQL evidence-gate rehearsal in a random rollback-only schema.
/// Tests the added trigger in isolation from existing independent signer and
/// official-invoice guards; those have their own PostgreSQL suites.
/// </summary>
public sealed class Phase2G4CashBackedDebtReviewPostgreSql_Tests
{
    private static readonly string UpSql = Assert.Single(
        new Phase2G4RequireCashBackedDebtApproval().UpOperations
            .OfType<SqlOperation>()).Sql;
    private static readonly string DownSql = Assert.Single(
        new Phase2G4RequireCashBackedDebtApproval().DownOperations
            .OfType<SqlOperation>()).Sql;

    private const string Tables = """
        CREATE TABLE "BuildingManagementContracts" ("Id" uuid PRIMARY KEY);
        CREATE TABLE "BuildingManagementPayments" (
            "Id" uuid PRIMARY KEY, "Amount" numeric(18,0) NOT NULL,
            "ContractId" uuid, "RoomReservationId" uuid,
            "Purpose" integer NOT NULL, "Status" integer NOT NULL,
            "IsDeleted" boolean NOT NULL DEFAULT FALSE,
            "PaidAt" timestamptz, "DepositDisposition" integer);
        CREATE TABLE "BuildingManagementDepositSettlements" (
            "Id" uuid PRIMARY KEY, "Type" integer NOT NULL,
            "Status" integer NOT NULL, "Amount" numeric(18,0) NOT NULL,
            "PaymentId" uuid, "SourceRoomReservationId" uuid,
            "SourceContractId" uuid, "TargetContractId" uuid);
        CREATE TABLE "BuildingManagementContractDepositDebtReviews" (
            "Id" uuid PRIMARY KEY, "ContractId" uuid NOT NULL,
            "VerifiedHeldAmount" numeric(18,0) NOT NULL);
        CREATE TABLE "BuildingManagementContractDepositDebtOffsetApprovals" (
            "Id" uuid PRIMARY KEY, "ContractId" uuid NOT NULL,
            "VerifiedHeldAmount" numeric(18,0) NOT NULL);
        """;

    [Fact]
    public async Task OI04CASH01_Declared_Deposit_Without_Completed_Allocation_Denied()
    {
        await Scratch(async (db, tx, contract, _) =>
        {
            await Denied(db, tx, InsertReview(contract, 3_000_000m), "23514");
            await Denied(db, tx, InsertApproval(contract, 3_000_000m), "23514");
        });
    }

    [Fact]
    public async Task OI04CASH02_Completed_Receipt_Allocated_Exactly_Once_Allows_Both_Signoffs()
    {
        await Scratch(async (db, tx, contract, payment) =>
        {
            await SeedDeposit(db, tx, contract, payment, 3_000_000m);
            await Run(db, tx, InsertReview(contract, 3_000_000m));
            await Run(db, tx, InsertApproval(contract, 3_000_000m));
            Assert.Equal(1L, await Count(db, tx,
                """SELECT count(*) FROM "BuildingManagementContractDepositDebtReviews" """));
            Assert.Equal(1L, await Count(db, tx,
                """SELECT count(*) FROM "BuildingManagementContractDepositDebtOffsetApprovals" """));
            await Denied(db, tx, DownSql, "23514");
        });
    }

    [Fact]
    public async Task OI04CASH03_Amount_Or_Receipt_Status_Or_Allocation_Change_Blocks_Signing()
    {
        await Scratch(async (db, tx, contract, payment) =>
        {
            await SeedDeposit(db, tx, contract, payment, 3_000_000m);
            await Denied(db, tx, InsertReview(contract, 4_000_000m), "23514");
            await Run(db, tx, $"""
                UPDATE "BuildingManagementDepositSettlements"
                   SET "Amount"=2000000 WHERE "PaymentId"='{payment:D}'
                """);
            await Denied(db, tx, InsertReview(contract, 2_000_000m), "23514");
            await Run(db, tx, $"""
                UPDATE "BuildingManagementDepositSettlements"
                   SET "Amount"=3000000 WHERE "PaymentId"='{payment:D}'
                """);
            await Run(db, tx, $"""
                UPDATE "BuildingManagementPayments" SET "Status"=1
                WHERE "Id"='{payment:D}'
                """);
            await Denied(db, tx, InsertReview(contract, 3_000_000m), "23514");
            await Run(db, tx, $"""
                UPDATE "BuildingManagementPayments" SET "Status"=2,"IsDeleted"=TRUE
                WHERE "Id"='{payment:D}'
                """);
            await Denied(db, tx, InsertReview(contract, 3_000_000m), "23514");
        });
    }

    [Fact]
    public async Task OI04CASH04_Payment_Reused_For_Other_Contract_Or_Refund_Denied()
    {
        await Scratch(async (db, tx, contract, payment) =>
        {
            await SeedDeposit(db, tx, contract, payment, 3_000_000m);
            var other = Guid.NewGuid();
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementContracts" VALUES ('{other:D}');
                INSERT INTO "BuildingManagementDepositSettlements"
                  VALUES ('{Guid.NewGuid():D}',3,2,1000000,'{payment:D}',NULL,NULL,'{other:D}')
                """);
            await Denied(db, tx, InsertReview(contract, 3_000_000m), "23514");
        });
        await Scratch(async (db, tx, contract, payment) =>
        {
            await SeedDeposit(db, tx, contract, payment, 3_000_000m);
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementDepositSettlements"
                  VALUES ('{Guid.NewGuid():D}',1,2,1000000,'{payment:D}',NULL,NULL,NULL)
                """);
            await Denied(db, tx, InsertApproval(contract, 3_000_000m), "23514");
        });
    }

    [Fact]
    public async Task OI04CASH05_Completed_Unallocated_Contract_Deposit_Is_Not_Ignored()
    {
        await Scratch(async (db, tx, contract, payment) =>
        {
            await SeedDeposit(db, tx, contract, payment, 3_000_000m);
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementPayments"
                  VALUES ('{Guid.NewGuid():D}',250000,'{contract:D}',NULL,
                          2,2,FALSE,now(),1)
                """);
            await Denied(db, tx, InsertReview(contract, 3_000_000m), "23514");
        });
    }

    [Fact]
    public async Task OI04CASH06_Valid_Reservation_Receipt_Requires_Matching_Source()
    {
        await Scratch(async (db, tx, contract, payment) =>
        {
            var reservation = Guid.NewGuid();
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementPayments"
                  VALUES ('{payment:D}',500000,NULL,'{reservation:D}',
                          1,2,FALSE,now(),2);
                INSERT INTO "BuildingManagementDepositSettlements"
                  VALUES ('{Guid.NewGuid():D}',3,2,500000,'{payment:D}',
                          '{reservation:D}',NULL,'{contract:D}');
                """);
            await Run(db, tx, InsertReview(contract, 500000m));
            await Run(db, tx, InsertApproval(contract, 500000m));
            await Run(db, tx, $"""
                UPDATE "BuildingManagementDepositSettlements"
                   SET "SourceRoomReservationId"='{Guid.NewGuid():D}'
                 WHERE "PaymentId"='{payment:D}'
                """);
            await Denied(db, tx, InsertReview(contract, 500000m), "23514");
        });
    }

    [Fact]
    public async Task OI04CASH07_Empty_Migration_Can_Downgrade_And_Reapply()
    {
        await Scratch(async (db, tx, _, _) =>
        {
            await Run(db, tx, DownSql);
            await Run(db, tx, UpSql);
        });
    }

    private static string InsertReview(Guid contract, decimal held) => $"""
        INSERT INTO "BuildingManagementContractDepositDebtReviews" VALUES
        ('{Guid.NewGuid():D}','{contract:D}',{held})
        """;

    private static string InsertApproval(Guid contract, decimal held) => $"""
        INSERT INTO "BuildingManagementContractDepositDebtOffsetApprovals" VALUES
        ('{Guid.NewGuid():D}','{contract:D}',{held})
        """;

    private static async Task SeedDeposit(NpgsqlConnection db,
        NpgsqlTransaction tx, Guid contract, Guid payment, decimal amount)
    {
        await Run(db, tx, $"""
            INSERT INTO "BuildingManagementPayments"
              VALUES ('{payment:D}',{amount},'{contract:D}',NULL,
                      2,2,FALSE,now(),1);
            INSERT INTO "BuildingManagementDepositSettlements"
              VALUES ('{Guid.NewGuid():D}',3,2,{amount},'{payment:D}',NULL,
                      NULL,'{contract:D}');
            """);
    }

    private static async Task Scratch(
        Func<NpgsqlConnection,NpgsqlTransaction,Guid,Guid,Task> scenario)
    {
        var cs = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        await using var db = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs)
            { Pooling = false, CommandTimeout = 45 }.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, db.Database);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable);
        var schema = "bm_oi04_cash_" + Guid.NewGuid().ToString("N");
        var contract = Guid.NewGuid();
        var payment = Guid.NewGuid();
        try
        {
            await Run(db, tx, $"""CREATE SCHEMA "{schema}" """);
            await Run(db, tx, $"""SET LOCAL search_path TO "{schema}" """);
            await Run(db, tx, Tables);
            await Run(db, tx, $"""
                INSERT INTO "BuildingManagementContracts" VALUES ('{contract:D}')
                """);
            await Run(db, tx, UpSql);
            await scenario(db, tx, contract, payment);
        }
        finally { await tx.RollbackAsync(); }
        Assert.Equal(0L, await Count(db, null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname='{schema}'"));
    }

    private static async Task Denied(NpgsqlConnection db,
        NpgsqlTransaction tx, string sql, string state)
    {
        await Run(db, tx, "SAVEPOINT oi04_cash");
        string? failure = null;
        try { await Run(db, tx, sql); }
        catch (PostgresException e) { failure = e.SqlState; }
        finally
        {
            await Run(db, tx, "ROLLBACK TO SAVEPOINT oi04_cash");
            await Run(db, tx, "RELEASE SAVEPOINT oi04_cash");
        }
        Assert.Equal(state, failure);
    }

    private static async Task Run(NpgsqlConnection db,
        NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> Count(NpgsqlConnection db,
        NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
