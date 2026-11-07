using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace NgocTrangHouseManagementSystem.PayOs.Staging.Tests;

/// <summary>
/// EXERCISES the actual source migration SQL against a random schema in the
/// dedicated disposable PostgreSQL 16 database. Everything ROLLS BACK.
/// The tables are real D04 legacy table names, but no production DB or
/// actual client Payment is ever touched. No financial writer is invoked.
/// </summary>
public sealed class PayOsStagingD04LegacyJournalSchema_Tests
{
    [Fact]
    public async Task Real_legacy_schema_enforces_immutable_review_approval_unique_receipt_and_outbox()
    {
        if (Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_DB") != "1" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PASSWORD") != "disposable-only" ||
            Environment.GetEnvironmentVariable("BM_PAYOS_EF_TEST_PORT") != "15432")
            throw new InvalidOperationException(
                "D04 ledger rehearsal MUST use the dedicated disposable PostgreSQL 16 test container.");

        const string connectionString =
            "Host=127.0.0.1;Port=15432;Database=payos_ef_migration_test;" +
            "Username=postgres;Password=disposable-only;Pooling=false;Timeout=5;Command Timeout=45";
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        Assert.Equal("payos_ef_migration_test", db.Database);

        await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable);
        var schema = "d04_journal_" + Guid.NewGuid().ToString("N");
        try
        {
            await ExecAsync(db, tx, $"CREATE SCHEMA \"{schema}\";");
            await ExecAsync(db, tx, $"SET LOCAL search_path TO \"{schema}\", public;");
            // Minimal trusted FK roots; the migration itself uses the REAL
            // BuildingManagement table/column names and does not create cash.
            await ExecAsync(db, tx, """
                CREATE TABLE "BuildingManagementContracts" ("Id" uuid PRIMARY KEY,"Status" integer NOT NULL);
                CREATE TABLE "BuildingManagementPayments" (
                    "Id" uuid PRIMARY KEY, "ContractId" uuid NOT NULL,
                    "Purpose" integer NOT NULL, "Method" integer NOT NULL,
                    "Channel" integer NOT NULL, "Status" integer NOT NULL,
                    "PaidAt" timestamptz, "IsDeleted" boolean NOT NULL,
                    "DepositDisposition" integer,
                    "Amount" numeric(18,0) NOT NULL,
                    "TransactionReference" varchar(128));
                CREATE TABLE "BuildingManagementDepositSettlements" (
                    "Id" uuid PRIMARY KEY, "PaymentId" uuid NOT NULL,
                    "TargetContractId" uuid NOT NULL,
                    "Type" integer NOT NULL, "Status" integer NOT NULL,
                    "Amount" numeric(18,0) NOT NULL);
                -- Existing Phase 2G.4 global bank reference claim table.
                -- Here we only supply the minimum fields used by D04 guard.
                CREATE TABLE "BuildingManagementBankEvidenceClaims" (
                    "NormalizedReference" varchar(128) PRIMARY KEY,
                    "SourceKind" varchar(20) NOT NULL,
                    "SourceId" uuid NOT NULL);
                """);

            var migration = new StagePayOsD04LegacySourceJournal();
            var up = Assert.Single(migration.UpOperations.OfType<SqlOperation>()).Sql;
            await ExecAsync(db, tx, up);

            var count = await ScalarAsync(db, tx, """
                SELECT count(*) FROM pg_tables
                 WHERE schemaname = current_schema()
                   AND tablename IN (
                     'BuildingManagementPayOsD04BankReviews',
                     'BuildingManagementPayOsD04OwnerApprovals',
                     'BuildingManagementPayOsD04ReceiptJournal',
                     'BuildingManagementPayOsD04Outbox');
                """);
            Assert.Equal(4, count);

            var indexed = await ScalarAsync(db, tx, """
                SELECT count(*) FROM pg_indexes
                 WHERE schemaname = current_schema() AND indexname IN
                  ('UX_PayOsD04Review_Intent','UX_PayOsD04Review_Payment',
                   'UX_PayOsD04Review_BankReference','UX_PayOsD04Approval_Review',
                   'UX_PayOsD04Journal_Intent','UX_PayOsD04Journal_Payment',
                   'UX_PayOsD04Journal_Settlement','UX_PayOsD04Journal_SourceKey',
                   'UX_PayOsD04Journal_BankReference',
                   'UX_PayOsD04Outbox_Receipt','UX_PayOsD04Outbox_SourceKey');
                """);
            Assert.Equal(11, indexed);

            var intent = Guid.NewGuid();
            var building = Guid.NewGuid();
            var payment = Guid.NewGuid();
            var contract = Guid.NewGuid();
            var settlement = Guid.NewGuid();
            var review = Guid.NewGuid();
            var approval = Guid.NewGuid();
            var receipt = Guid.NewGuid();
            var outbox = Guid.NewGuid();
            var reviewer = Guid.NewGuid();
            var owner = Guid.NewGuid();
            var reference = "SYNTH-D04-" + Guid.NewGuid().ToString("N");
            var source = "PAYOS:D04:" + intent.ToString("N");

            await ExecAsync(db, tx, $"""
                INSERT INTO "BuildingManagementContracts"("Id","Status") VALUES ('{contract}',5);
                INSERT INTO "BuildingManagementPayments"
                ("Id","ContractId","Purpose","Method","Channel","Status",
                 "PaidAt","IsDeleted","DepositDisposition","Amount","TransactionReference")
                VALUES ('{payment}','{contract}',2,2,2,2,now(),false,1,150000,'{reference}');
                INSERT INTO "BuildingManagementDepositSettlements"
                ("Id","PaymentId","TargetContractId","Type","Status","Amount")
                VALUES ('{settlement}','{payment}','{contract}',3,2,150000);
                INSERT INTO "BuildingManagementBankEvidenceClaims"
                ("NormalizedReference","SourceKind","SourceId")
                VALUES (lower('{reference}'),'Payment','{payment}');
                INSERT INTO "BuildingManagementPayOsD04BankReviews"
                ("Id","PayOsIntentId","OrderCode","BuildingId","PaymentId","AmountVnd",
                 "BankReference","DocumentSha256","ExternalBankProofReference",
                 "ReviewerId","VerifiedAtUtc")
                VALUES ('{review}','{intent}',91551001,'{building}','{payment}',150000,
                        '{reference}',repeat('a',64),'BANK-STATEMENT-DOCUMENT',
                        '{reviewer}',now());
                INSERT INTO "BuildingManagementPayOsD04OwnerApprovals"
                ("Id","BankReviewId","ReviewerId","DocumentSha256",
                 "OwnerApproverId","ApprovedAtUtc","ExpiresAtUtc")
                VALUES ('{approval}','{review}','{reviewer}',repeat('a',64),
                        '{owner}',now(),now()+interval '1 day');
                INSERT INTO "BuildingManagementPayOsD04ReceiptJournal"
                ("Id","PayOsIntentId","OrderCode","BuildingId","ContractId","PaymentId",
                 "DepositSettlementId","BankReviewId","OwnerApprovalId",
                 "SourceKey","BankReference","AmountVnd","DocumentSha256",
                 "ReviewerId","OwnerApproverId","CreatedAtUtc")
                VALUES ('{receipt}','{intent}',91551001,'{building}','{contract}','{payment}',
                        '{settlement}','{review}','{approval}','{source}',
                        '{reference}',150000,repeat('a',64),
                        '{reviewer}','{owner}',now());
                INSERT INTO "BuildingManagementPayOsD04Outbox"
                ("Id","ReceiptId","SourceKey","EventType","CreatedAtUtc")
                VALUES ('{outbox}','{receipt}','{source}',
                        'D04PayOsReceiptCommitted',now());
                """);

            // Test negative mutations within PostgreSQL subtransactions;
            // no failed SQL is allowed to abort the outer rollback-only test.
            await ExecAsync(db, tx, $"""
                DO $tests$
                DECLARE refused boolean;
                BEGIN
                  refused := false;
                  BEGIN
                    UPDATE "BuildingManagementPayOsD04BankReviews"
                       SET "AmountVnd"=1 WHERE "Id"='{review}';
                  EXCEPTION WHEN check_violation THEN refused := true; END;
                  IF NOT refused THEN RAISE EXCEPTION 'Review mutation was accepted'; END IF;

                  refused := false;
                  BEGIN
                    UPDATE "BuildingManagementPayOsD04ReceiptJournal"
                       SET "AmountVnd"=1 WHERE "Id"='{receipt}';
                  EXCEPTION WHEN check_violation THEN refused := true; END;
                  IF NOT refused THEN RAISE EXCEPTION 'Receipt mutation was accepted'; END IF;

                  refused := false;
                  BEGIN
                    UPDATE "BuildingManagementPayOsD04Outbox"
                       SET "SourceKey"='PAYOS:D04:FORGED' WHERE "Id"='{outbox}';
                  EXCEPTION WHEN check_violation THEN refused := true; END;
                  IF NOT refused THEN RAISE EXCEPTION 'Outbox source mutation accepted'; END IF;

                  refused := false;
                  BEGIN
                    INSERT INTO "BuildingManagementPayOsD04OwnerApprovals"
                    ("Id","BankReviewId","ReviewerId","DocumentSha256",
                     "OwnerApproverId","ApprovedAtUtc","ExpiresAtUtc")
                    VALUES (gen_random_uuid(),'{review}','{reviewer}',
                            repeat('a',64),'{reviewer}',now(),now()+interval '1 day');
                  EXCEPTION WHEN check_violation THEN refused := true; END;
                  IF NOT refused THEN RAISE EXCEPTION 'Same-person approval accepted'; END IF;
                END;
                $tests$;
                """);

            await ExecAsync(db, tx, $"""
                UPDATE "BuildingManagementPayOsD04Outbox"
                   SET "AcknowledgedAtUtc"=now(),"AttemptCount"=1
                 WHERE "Id"='{outbox}';
                """);
            Assert.Equal(1, await ScalarAsync(db, tx,
                $"""SELECT count(*) FROM "BuildingManagementPayOsD04Outbox" WHERE "Id"='{outbox}' AND "AcknowledgedAtUtc" IS NOT NULL AND "AttemptCount"=1"""));

            // Rollback the entire synthetic journal plus its temporary schema.
            await tx.RollbackAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private static async Task ExecAsync(
        NpgsqlConnection db, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(
        NpgsqlConnection db, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        return (long)(await cmd.ExecuteScalarAsync() ??
            throw new InvalidOperationException("Database scalar returned NULL."));
    }
}
