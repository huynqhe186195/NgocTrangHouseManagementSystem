using System;
using System.Linq;
using System.Globalization;
using BuildingManagement.Invoices;
using BuildingManagement.Utilities;
using System.Text.Json;
using System.Threading.Tasks;
using BuildingManagement.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace BuildingManagement.PostgreSqlTests;

/// <summary>
/// Exercises real chained migration SQL on a transaction-local random
/// PostgreSQL schema. Never touches existing database objects or commits.
/// This confirms candidate accounting, NOT recognized customer debt.
/// </summary>
public sealed class SliceBReconciliationBoundaryPostgreSql_Tests
{
    private sealed record Fixture(
        Guid Draft, Guid Request, Guid Evidence, Guid Review,
        Guid Invoice, Guid Contract, Guid Room, Guid Building, string Hash);

    [Fact]
    public async Task SBRECON01_Complete_Receipt_Journal_Has_Bounded_Candidate_Balances()
    {
        await InScratchAsync(async (db, tx, down) =>
        {
            var f = await SeedIssuedAsync(db, tx);
            var first = Guid.NewGuid();
            await SeedPaymentAsync(db, tx, f, first, 250_000m);
            await InsertAllocationAsync(db, tx, f, first, 250_000m);

            await AssertBalanceAsync(db, tx, f.Invoice,
                staged: 250_000m, remaining: 750_000m,
                count: 1, allStaged: false);
            await ExpectFailureAsync(db, tx,
                AllocationSql(f, first, 250_000m), "23505");

            var other = Guid.NewGuid();
            await SeedPaymentAsync(db, tx, f, other, 750_000m);
            await InsertAllocationAsync(db, tx, f, other, 750_000m);
            await AssertBalanceAsync(db, tx, f.Invoice,
                staged: 1_000_000m, remaining: 0m,
                count: 2, allStaged: true);

            // Even a zero candidate balance is NOT posted debt clearance,
            // not a deposit offset, and not permission to release the room.
            await using var cmd = new NpgsqlCommand("""
                SELECT "IsDebtLegallyCleared", "MaySettleDeposit", "MayCompleteMoveOut"
                  FROM "BuildingManagementInvoiceAllocationCandidateBalances"
                 WHERE "IssuedInvoiceId" = @id
                """, db, tx);
            cmd.Parameters.AddWithValue("id", f.Invoice);
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.False(reader.GetBoolean(0));
                Assert.False(reader.GetBoolean(1));
                Assert.False(reader.GetBoolean(2));
            }

            var extra = Guid.NewGuid();
            await SeedPaymentAsync(db, tx, f, extra, 1m);
            await ExpectFailureAsync(db, tx,
                AllocationSql(f, extra, 1m), "23514");
            await ExpectFailureAsync(db, tx, down, "23514");
            await AssertBalanceAsync(db, tx, f.Invoice,
                staged: 1_000_000m, remaining: 0m,
                count: 2, allStaged: true);
        });
    }

    [Fact]
    public async Task SBRECON02_Rejects_Unissued_Draft_Mismatched_Receipt_And_Overpayment()
    {
        await InScratchAsync(async (db, tx, _) =>
        {
            var f = await SeedIssuedAsync(db, tx);
            var payment = Guid.NewGuid();
            await SeedPaymentAsync(db, tx, f, payment, 500_000m);

            await ExpectFailureAsync(db, tx,
                AllocationSql(f, payment, 500_000m)
                    .Replace($"'{f.Invoice:D}'", $"'{f.Draft:D}'",
                        StringComparison.Ordinal), "23514");
            await ExpectFailureAsync(db, tx,
                AllocationSql(f, payment, 500_001m), "23514");
            await ExpectFailureAsync(db, tx,
                AllocationSql(f, payment, 500_000m)
                    .Replace($"'{f.Contract:D}'", $"'{Guid.NewGuid():D}'",
                        StringComparison.Ordinal), "23514");
            await ExpectFailureAsync(db, tx,
                AllocationSql(f, payment, 500_000m)
                    .Replace("'2026-10-29T02:00:00Z'",
                        "'2026-10-28T22:00:00Z'",
                        StringComparison.Ordinal), "23514");
            await InsertAllocationAsync(db, tx, f, payment, 500_000m);

            var second = Guid.NewGuid();
            await SeedPaymentAsync(db, tx, f, second, 600_000m);
            await ExpectFailureAsync(db, tx,
                AllocationSql(f, second, 600_000m), "23514");

            Assert.Equal(1L, await ScalarAsync(db, tx,
                "SELECT count(*) FROM \"BuildingManagementInvoicePaymentAllocationJournal\""));
        });
    }

    [Fact]
    public async Task SBRECON03_Missing_Terms_WholeVnd_And_Unsafe_Down_Are_Rejected()
    {
        await InScratchAsync(async (db, tx, down) =>
        {
            var f = await SeedIssuedAsync(db, tx);
            await ExpectFailureAsync(db, tx, down, "23514");
            var jsonMissingTotal = JsonSerializer.Serialize(new
            {
                DraftInvoiceId = f.Draft,
                EvidenceSha256 = f.Hash,
                BaseAmount = 1_000_000m,
                AuxiliaryAmount = 0m,
                DueDate = "2026-11-05"
            });
            // The immutable candidate cannot be made internally
            // inconsistent with its JSON via a late UPDATE.
            await ExpectFailureAsync(db, tx,
                "UPDATE \"BuildingManagementIssuedInvoiceLedger\" " +
                $"SET \"FinancialTermsJson\" = '{jsonMissingTotal}'", "23514");
            Assert.Equal(1L, await ScalarAsync(db, tx,
                "SELECT count(*) FROM \"BuildingManagementIssuedInvoiceLedger\""));
        });

        await InScratchAsync(async (db, tx, down) =>
        {
            await ExecuteAsync(db, tx, down);
            Assert.Equal(1L, await ScalarAsync(db, tx, """
                SELECT to_regclass('"BuildingManagementInvoiceAllocationCandidateBalances"') IS NULL
                """));
            Assert.Equal(1L, await ScalarAsync(db, tx, """
                SELECT to_regclass('"BuildingManagementInvoicePaymentAllocationJournal"') IS NOT NULL
                """));
            Assert.Equal(1L, await ScalarAsync(db, tx, """
                SELECT to_regclass('"BuildingManagementIssuedInvoiceLedger"') IS NOT NULL
                """));
        });
    }

    [Fact]
    public async Task SBRECON04_Case_And_Whitespace_Ref_Replay_Is_Unique_In_Database()
    {
        await InScratchAsync(async (db, tx, _) =>
        {
            var f = await SeedIssuedAsync(db, tx);
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            await SeedPaymentAsync(db, tx, f, first, 100_000m);
            await SeedPaymentAsync(db, tx, f, second, 200_000m);

            // Before the migration, the two receipts are deliberately
            // distinct objects with colliding normalized bank references.
            await ExecuteAsync(db, tx, $"""
                UPDATE "BuildingManagementPayments"
                   SET "TransactionReference" = 'BANK-REPLAY-001'
                 WHERE "Id" = '{first:D}';
                UPDATE "BuildingManagementPayments"
                   SET "TransactionReference" = ' bank-replay-001 '
                 WHERE "Id" = '{second:D}';
                """);
            var migration = new SliceBNormalizedInvoiceReceiptReferences();
            await ExpectFailureAsync(db, tx, Sql(migration, false), "23505");

            await ExecuteAsync(db, tx, $"""
                UPDATE "BuildingManagementPayments"
                   SET "TransactionReference" = 'UNRELATED-BANK-REF-001'
                 WHERE "Id" = '{second:D}';
                """);
            await ExecuteAsync(db, tx, Sql(migration, false));

            // Normalized uniqueness must be enforced by PostgreSQL,
            // including direct UPDATEs that bypass any HTTP preflight.
            await ExpectFailureAsync(db, tx, $"""
                UPDATE "BuildingManagementPayments"
                   SET "TransactionReference" = '  bank-replay-001  '
                 WHERE "Id" = '{second:D}';
                """, "23505");
            Assert.Equal(2L, await ScalarAsync(db, tx,
                "SELECT count(*) FROM \"BuildingManagementPayments\""));

            await ExecuteAsync(db, tx, Sql(migration, true));
            await ExecuteAsync(db, tx, $"""
                UPDATE "BuildingManagementPayments"
                   SET "TransactionReference" = ' bank-replay-001 '
                 WHERE "Id" = '{second:D}';
                """);
        });
    }

    [Fact]
    public async Task SBRECON05_Nonempty_Journal_Refuses_ReceiptIndex_Rollback()
    {
        await InScratchAsync(async (db, tx, _) =>
        {
            var f = await SeedIssuedAsync(db, tx);
            var payment = Guid.NewGuid();
            await SeedPaymentAsync(db, tx, f, payment, 100_000m);
            await InsertAllocationAsync(db, tx, f, payment, 100_000m);

            var migration = new SliceBNormalizedInvoiceReceiptReferences();
            await ExecuteAsync(db, tx, Sql(migration, false));
            await ExpectFailureAsync(db, tx, Sql(migration, true), "23514");
            Assert.Equal(1L, await ScalarAsync(db, tx, """
                SELECT count(*)
                  FROM pg_indexes
                 WHERE schemaname = current_schema()
                   AND indexname = 'UX_BM_InvoicePayment_NormalizedBankReference'
                """));
        });
    }

    [Fact]
    public async Task SBRECON06_Monthly_Candidate_Is_Immutable_Duplicates_And_Down_Refused()
    {
        await InScratchAsync(async (db, tx, _) =>
        {
            var draft = Guid.NewGuid();
            var contract = Guid.NewGuid();
            var room = Guid.NewGuid();
            await ExecuteAsync(db, tx, $"""
                INSERT INTO "BuildingManagementInvoices"
                  ("Id","ContractId","RoomId","BillingYear","BillingMonth","Status")
                VALUES ('{draft:D}','{contract:D}','{room:D}',2026,10,0);
                """);
            var source = MonthlyEvidence(draft, contract, room);
            var candidate = MonthlyInvoiceCandidateLedgerRecord.Capture(
                Guid.NewGuid(), source);
            var migration = new Phase2G3MonthlyCandidateEvidence();
            await ExecuteAsync(db, tx, Sql(migration, false));

            await ExecuteAsync(db, tx, MonthlyInsertSql(candidate));
            Assert.Equal(1L, await ScalarAsync(db, tx, """
                SELECT count(*) FROM "BuildingManagementMonthlyInvoiceCandidates"
                """));

            await ExpectFailureAsync(db, tx,
                MonthlyInsertSql(MonthlyInvoiceCandidateLedgerRecord.Capture(
                    Guid.NewGuid(), source)), "23505");
            await ExpectFailureAsync(db, tx, $"""
                UPDATE "BuildingManagementMonthlyInvoiceCandidates"
                   SET "RentAmount" = 1
                 WHERE "Id" = '{candidate.Id:D}';
                """, "23514");
            await ExpectFailureAsync(db, tx, $"""
                DELETE FROM "BuildingManagementMonthlyInvoiceCandidates"
                 WHERE "Id" = '{candidate.Id:D}';
                """, "23514");
            await ExpectFailureAsync(db, tx, """
                TRUNCATE "BuildingManagementMonthlyInvoiceCandidates"
                """, "23514");
            await ExpectFailureAsync(db, tx, Sql(migration, true), "23514");
        });
    }

    [Fact]
    public async Task SBRECON08_Persisted_Utc_Review_Time_Matches_Frozen_Monthly_Evidence()
    {
        await InScratchAsync(async (db, tx, _) =>
        {
            var draft = Guid.NewGuid();
            var contract = Guid.NewGuid();
            var room = Guid.NewGuid();
            await ExecuteAsync(db, tx, $"""
                INSERT INTO "BuildingManagementInvoices"
                  ("Id","ContractId","RoomId","BillingYear","BillingMonth","Status")
                VALUES ('{draft:D}','{contract:D}','{room:D}',2026,10,0);
                """);
            var snapshot = MonthlyEvidence(draft, contract, room);
            var unusual = snapshot with
            {
                ReviewedAtUtc = snapshot.ReviewedAtUtc.AddTicks(7)
            };
            var archived = MonthlyInvoiceCandidateLedgerRecord.Capture(
                Guid.NewGuid(), unusual);
            await ExecuteAsync(db, tx,
                Sql(new Phase2G3MonthlyCandidateEvidence(), false));
            await ExecuteAsync(db, tx, MonthlyInsertSql(archived));

            await using var cmd = new NpgsqlCommand("""
                SELECT "ReviewedAtUtc"
                  FROM "BuildingManagementMonthlyInvoiceCandidates"
                 WHERE "Id" = @id
                """, db, tx);
            cmd.Parameters.AddWithValue("id", archived.Id);
            var storedTime = (DateTime)(await cmd.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("No monthly evidence saved."));
            Assert.Equal(DateTimeKind.Utc, storedTime.Kind);
            Assert.Equal(archived.ReviewedAtUtc, storedTime);

            // The actual persisted timestamp is still consistent with the
            // archived SHA-256 snapshot, not just the original object graph.
            typeof(MonthlyInvoiceCandidateLedgerRecord)
                .GetProperty(nameof(MonthlyInvoiceCandidateLedgerRecord.ReviewedAtUtc))!
                .SetValue(archived, storedTime);
            Assert.Equal(storedTime, archived.VerifyStoredEvidence().ReviewedAtUtc);
        });
    }

    [Fact]
    public async Task SBRECON07_Monthly_Candidate_Refuses_Issued_Draft_Period()
    {
        await InScratchAsync(async (db, tx, _) =>
        {
            var issued = await SeedIssuedAsync(db, tx);
            var candidate = MonthlyInvoiceCandidateLedgerRecord.Capture(
                Guid.NewGuid(), MonthlyEvidence(
                    issued.Draft, issued.Contract, issued.Room));
            var migration = new Phase2G3MonthlyCandidateEvidence();
            await ExecuteAsync(db, tx, Sql(migration, false));
            await ExpectFailureAsync(
                db, tx, MonthlyInsertSql(candidate), "23514");
            Assert.Equal(0L, await ScalarAsync(db, tx, """
                SELECT count(*) FROM "BuildingManagementMonthlyInvoiceCandidates"
                """));
            await ExecuteAsync(db, tx, Sql(migration, true));
        });
    }

    private static MonthlyInvoiceCandidateSnapshot MonthlyEvidence(
        Guid draft, Guid contract, Guid room)
    {
        var lines = new[]
        {
            new MonthlyInvoiceCandidateUtilityLine(
                UtilityType.Electricity, Guid.NewGuid(), 10m,
                3_500m, 35_000m,
                new[] { new MonthlyInvoiceCandidateMeterSource(
                    Guid.NewGuid(), Guid.NewGuid(),
                    MeterUsageSourceType.MonthlyReading,
                    new DateTime(2026, 10, 31), 100m, 110m) }),
            new MonthlyInvoiceCandidateUtilityLine(
                UtilityType.Water, Guid.NewGuid(), 1m,
                15_000m, 15_000m,
                new[] { new MonthlyInvoiceCandidateMeterSource(
                    Guid.NewGuid(), Guid.NewGuid(),
                    MeterUsageSourceType.MonthlyReading,
                    new DateTime(2026, 10, 31), 50m, 51m) })
        };
        return new MonthlyInvoiceCandidateSnapshot(
            draft, contract, room, Guid.NewGuid(), 2026, 10,
            Guid.NewGuid(), new DateTime(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc),
            new DateOnly(2026, 11, 5), "MONTHLY-APPROVED-DUE",
            3_000_000m, 35_000m, 15_000m,
            AuxiliaryFeeAssessmentDecision.ExplicitlyNoCharges,
            "MONTHLY-APPROVED-NO-SERVICE-FEES",
            Array.Empty<FinalInvoiceAuxiliaryChargeProposal>(),
            0m, 3_050_000m, lines);
    }

    private static string MonthlyInsertSql(
        MonthlyInvoiceCandidateLedgerRecord x)
    {
        var quotedJson = x.FrozenEvidenceJson.Replace("'", "''", StringComparison.Ordinal);
        return $"""
            INSERT INTO "BuildingManagementMonthlyInvoiceCandidates"
              ("Id","DraftInvoiceId","ContractId","RoomId","BuildingId",
               "BillingYear","BillingMonth","ReviewedByUserId","ReviewedAtUtc",
               "DueDate","RentAmount","ElectricityAmount","WaterAmount",
               "AuxiliaryAmount","TotalAmount",
               "FrozenEvidenceJson","FrozenEvidenceSha256")
            VALUES
              ('{x.Id:D}','{x.DraftInvoiceId:D}','{x.ContractId:D}',
               '{x.RoomId:D}','{x.BuildingId:D}',
               {x.BillingYear},{x.BillingMonth},
               '{x.ReviewedByUserId:D}','{x.ReviewedAtUtc:O}',
               '{x.DueDate:yyyy-MM-dd}',
               {x.RentAmount.ToString(CultureInfo.InvariantCulture)},
               {x.ElectricityAmount.ToString(CultureInfo.InvariantCulture)},
               {x.WaterAmount.ToString(CultureInfo.InvariantCulture)},
               {x.AuxiliaryAmount.ToString(CultureInfo.InvariantCulture)},
               {x.TotalAmount.ToString(CultureInfo.InvariantCulture)},
               '{quotedJson}','{x.FrozenEvidenceSha256}');
            """;
    }

    [Fact]
    public async Task SBRECON09_Independent_Monthly_Issue_Immutable_Duplicates_Rollback()
    {
        await InScratchAsync(async (db, tx, _) =>
        {
            var draft = Guid.NewGuid();
            var contract = Guid.NewGuid();
            var room = Guid.NewGuid();
            await ExecuteAsync(db, tx, $"""
                INSERT INTO "BuildingManagementInvoices"
                  ("Id","ContractId","RoomId","BillingYear","BillingMonth","Status")
                VALUES ('{draft:D}','{contract:D}','{room:D}',2026,10,0);
                """);
            var archive = MonthlyInvoiceCandidateLedgerRecord.Capture(
                Guid.NewGuid(), MonthlyEvidence(draft, contract, room));
            await ExecuteAsync(db, tx, Sql(new Phase2G3MonthlyCandidateEvidence(), false));
            await ExecuteAsync(db, tx, MonthlyInsertSql(archive));

            var migration = new Phase2G3MonthlyIndependentIssuance();
            await ExecuteAsync(db, tx, Sql(migration, false));
            var review = MonthlyInvoiceIndependentReviewRecord.Capture(
                Guid.NewGuid(), archive, Guid.NewGuid(),
                archive.ReviewedAtUtc.AddHours(1), true,
                "Independent full-month rent and meter review");
            await ExecuteAsync(db, tx, MonthlyReviewInsertSql(review));

            var issued = MonthlyIssuedInvoiceRecord.Issue(
                Guid.NewGuid(), "NTM-TEST-" + Guid.NewGuid().ToString("N"),
                archive.ReviewedByUserId, review.ReviewedAtUtc.AddHours(1),
                archive, review);
            await ExecuteAsync(db, tx, MonthlyIssuedInsertSql(issued));
            Assert.Equal(1L, await ScalarAsync(db, tx, """
                SELECT count(*) FROM "BuildingManagementMonthlyIssuedInvoiceLedger"
                """));

            await ExpectFailureAsync(db, tx,
                MonthlyIssuedInsertSql(MonthlyIssuedInvoiceRecord.Issue(
                    Guid.NewGuid(), "NTM-TEST-" + Guid.NewGuid().ToString("N"),
                    archive.ReviewedByUserId, review.ReviewedAtUtc.AddHours(2),
                    archive, review)), "23505");
            await ExpectFailureAsync(db, tx, $"""
                UPDATE "BuildingManagementMonthlyIssuedInvoiceLedger"
                   SET "TotalAmount" = 1 WHERE "Id" = '{issued.Id:D}';
                """, "23514");
            await ExpectFailureAsync(db, tx, $"""
                DELETE FROM "BuildingManagementMonthlyInvoiceReviews"
                 WHERE "Id" = '{review.Id:D}';
                """, "23514");
            await ExpectFailureAsync(db, tx, """
                TRUNCATE "BuildingManagementMonthlyIssuedInvoiceLedger"
                """, "23514");
            await ExpectFailureAsync(db, tx, Sql(migration, true), "23514");
        });
    }

    [Fact]
    public async Task SBRECON10_Rejected_Or_SelfReviewed_Monthly_Cannot_Issue()
    {
        await InScratchAsync(async (db, tx, _) =>
        {
            var draft = Guid.NewGuid();
            var contract = Guid.NewGuid();
            var room = Guid.NewGuid();
            await ExecuteAsync(db, tx, $"""
                INSERT INTO "BuildingManagementInvoices"
                  ("Id","ContractId","RoomId","BillingYear","BillingMonth","Status")
                VALUES ('{draft:D}','{contract:D}','{room:D}',2026,10,0);
                """);
            var archive = MonthlyInvoiceCandidateLedgerRecord.Capture(
                Guid.NewGuid(), MonthlyEvidence(draft, contract, room));
            await ExecuteAsync(db, tx, Sql(new Phase2G3MonthlyCandidateEvidence(), false));
            await ExecuteAsync(db, tx, MonthlyInsertSql(archive));
            var migration = new Phase2G3MonthlyIndependentIssuance();
            await ExecuteAsync(db, tx, Sql(migration, false));

            var deniedReview = MonthlyInvoiceIndependentReviewRecord.Capture(
                Guid.NewGuid(), archive, Guid.NewGuid(),
                archive.ReviewedAtUtc.AddHours(1), false,
                "Archived sources failed independent pricing review");
            await ExecuteAsync(db, tx, MonthlyReviewInsertSql(deniedReview));

            var altered = MonthlyReviewInsertSql(deniedReview)
                .Replace($"'{deniedReview.Id:D}'", $"'{Guid.NewGuid():D}'",
                    StringComparison.Ordinal)
                .Replace($"'{deniedReview.ReviewedByUserId:D}'",
                    $"'{archive.ReviewedByUserId:D}'", StringComparison.Ordinal);
            await ExpectFailureAsync(db, tx, altered, "23514");

            // A forged or stale INSERT bypassing the DOMAIN factory still
            // cannot turn a rejected independent review into an invoice.
            var unsafeSql = $"""
                INSERT INTO "BuildingManagementMonthlyIssuedInvoiceLedger"
                    ("Id","CandidateId","ReviewId","DraftInvoiceId","ContractId",
                     "RoomId","BuildingId","BillingYear","BillingMonth",
                     "InvoiceNumber","IssuedByUserId","IssuedAtUtc","DueDate",
                     "TotalAmount","CandidateSha256")
                VALUES ('{Guid.NewGuid():D}','{archive.Id:D}',
                    '{deniedReview.Id:D}','{draft:D}','{contract:D}',
                    '{room:D}','{archive.BuildingId:D}',2026,10,
                    'NTM-UNSAFE-{Guid.NewGuid():N}',
                    '{archive.ReviewedByUserId:D}',
                    '{deniedReview.ReviewedAtUtc.AddHours(1):O}',
                    '{archive.DueDate:yyyy-MM-dd}',{archive.TotalAmount},
                    '{archive.FrozenEvidenceSha256}');
                """;
            await ExpectFailureAsync(db, tx, unsafeSql, "23514");
            Assert.Equal(0L, await ScalarAsync(db, tx, """
                SELECT count(*) FROM "BuildingManagementMonthlyIssuedInvoiceLedger"
                """));
            await ExpectFailureAsync(db, tx, Sql(migration, true), "23514");
        });
    }

    private static string MonthlyReviewInsertSql(
        MonthlyInvoiceIndependentReviewRecord x)
        => $"""
           INSERT INTO "BuildingManagementMonthlyInvoiceReviews"
               ("Id","CandidateId","ContractId","BuildingId",
                "CandidateSha256","PreparedByUserId","ReviewedByUserId",
                "ReviewedAtUtc","Accepted","Reason")
           VALUES ('{x.Id:D}','{x.CandidateId:D}',
                   '{x.ContractId:D}','{x.BuildingId:D}',
                   '{x.CandidateSha256}','{x.PreparedByUserId:D}',
                   '{x.ReviewedByUserId:D}','{x.ReviewedAtUtc:O}',
                   {(x.Accepted ? "TRUE" : "FALSE")},
                   '{x.Reason.Replace("'", "''", StringComparison.Ordinal)}');
           """;

    private static string MonthlyIssuedInsertSql(MonthlyIssuedInvoiceRecord x)
        => $"""
           INSERT INTO "BuildingManagementMonthlyIssuedInvoiceLedger"
               ("Id","CandidateId","ReviewId","DraftInvoiceId","ContractId",
                "RoomId","BuildingId","BillingYear","BillingMonth",
                "InvoiceNumber","IssuedByUserId","IssuedAtUtc","DueDate",
                "TotalAmount","CandidateSha256")
           VALUES ('{x.Id:D}','{x.CandidateId:D}','{x.ReviewId:D}',
                   '{x.DraftInvoiceId:D}','{x.ContractId:D}',
                   '{x.RoomId:D}','{x.BuildingId:D}',{x.BillingYear},
                   {x.BillingMonth},'{x.InvoiceNumber}',
                   '{x.IssuedByUserId:D}','{x.IssuedAtUtc:O}',
                   '{x.DueDate:yyyy-MM-dd}',
                   {x.TotalAmount.ToString(CultureInfo.InvariantCulture)},
                   '{x.CandidateSha256}');
           """;

    private static async Task AssertBalanceAsync(
        NpgsqlConnection db, NpgsqlTransaction tx, Guid issuedId,
        decimal staged, decimal remaining, long count, bool allStaged)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT "StagedAllocated", "CandidateUnallocated",
                   "StagedReceiptCount", "AllReceiptsAccountedInStaging"
              FROM "BuildingManagementInvoiceAllocationCandidateBalances"
             WHERE "IssuedInvoiceId" = @id
            """, db, tx);
        cmd.Parameters.AddWithValue("id", issuedId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(staged, reader.GetDecimal(0));
        Assert.Equal(remaining, reader.GetDecimal(1));
        Assert.Equal(count, reader.GetInt64(2));
        Assert.Equal(allStaged, reader.GetBoolean(3));
    }

    private static async Task<Fixture> SeedIssuedAsync(
        NpgsqlConnection db, NpgsqlTransaction tx)
    {
        var f = new Fixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), new string('a', 64));
        var submitter = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var prepared = Guid.NewGuid();
        var issuer = Guid.NewGuid();
        var terms = JsonSerializer.Serialize(new
        {
            DraftInvoiceId = f.Draft,
            EvidenceSha256 = f.Hash,
            BaseAmount = 1_000_000m,
            AuxiliaryAmount = 0m,
            TotalAmount = 1_000_000m,
            DueDate = "2026-11-05",
            DueDatePolicyReference = "DOCUMENTED-DUE-DATE-POLICY",
            AuxiliaryFeeDecision = 1,
            AuxiliaryFeePolicyReference = "EXPLICIT-NO-SERVICE-FEES",
            AuxiliaryFeeReviewerId = Guid.NewGuid(),
            AuxiliaryCharges = Array.Empty<object>()
        });
        await ExecuteAsync(db, tx, $"""
            INSERT INTO "BuildingManagementInvoices"
                ("Id","ContractId","RoomId","BillingYear","BillingMonth","Status")
            VALUES ('{f.Draft:D}','{f.Contract:D}','{f.Room:D}',2026,10,0);
            INSERT INTO "BuildingManagementMoveOutRequests" ("Id")
                VALUES ('{f.Request:D}');
            INSERT INTO "BuildingManagementFinalInvoiceIssueEvidence"
                ("Id","DraftInvoiceId","MoveOutRequestId","ContractId","RoomId",
                 "BillingYear","BillingMonth","PreparedByUserId","PreparedAtUtc",
                 "KnownBaseChargeTotal","EvidenceJson","EvidenceSha256")
            VALUES ('{f.Evidence:D}','{f.Draft:D}','{f.Request:D}',
                    '{f.Contract:D}','{f.Room:D}',2026,10,'{prepared:D}',
                    '2026-10-29T00:00:00Z',1000000,'[]','{f.Hash}');
            INSERT INTO "BuildingManagementInvoiceIssuanceReviews"
                ("Id","StagedEvidenceId","DraftInvoiceId","MoveOutRequestId",
                 "ContractId","RoomId","BuildingId","EvidenceSha256",
                 "SubmittedByUserId","SubmittedAtUtc","Status")
            VALUES ('{f.Review:D}','{f.Evidence:D}','{f.Draft:D}',
                    '{f.Request:D}','{f.Contract:D}','{f.Room:D}',
                    '{f.Building:D}','{f.Hash}','{submitter:D}',
                    '2026-10-29T00:10:00Z',1);
            UPDATE "BuildingManagementInvoiceIssuanceReviews"
               SET "Status"=2, "ReviewedByUserId"='{reviewer:D}',
                   "ReviewedAtUtc"='2026-10-29T00:30:00Z',
                   "ReviewReason"='Consistent independent design review'
             WHERE "Id"='{f.Review:D}';
            INSERT INTO "BuildingManagementIssuedInvoiceLedger"
                ("Id","DraftInvoiceId","StagedEvidenceId","ReviewTicketId",
                 "MoveOutRequestId","ContractId","RoomId","BuildingId",
                 "BillingYear","BillingMonth","InvoiceNumber","IssuedByUserId",
                 "IssuedAtUtc","DueDate","BaseAmount","AuxiliaryAmount",
                 "TotalAmount","EvidenceSha256","FinancialTermsJson",
                 "FinancialTermsSha256")
            VALUES ('{f.Invoice:D}','{f.Draft:D}','{f.Evidence:D}','{f.Review:D}',
                    '{f.Request:D}','{f.Contract:D}','{f.Room:D}',
                    '{f.Building:D}',2026,10,'STAGED-{f.Invoice:N}',
                    '{issuer:D}','2026-10-29T01:00:00Z',
                    '2026-11-05',1000000,0,1000000,'{f.Hash}',
                    '{terms}','{new string('b', 64)}');
            """);
        return f;
    }

    private static async Task SeedPaymentAsync(
        NpgsqlConnection db, NpgsqlTransaction tx, Fixture f,
        Guid paymentId, decimal amount)
    {
        await ExecuteAsync(db, tx, $"""
            INSERT INTO "BuildingManagementPayments"
                ("Id","Purpose","Status","ContractId","InvoiceId",
                 "Amount","PaymentNumber","PaidAt","TransactionReference","IsDeleted")
            VALUES ('{paymentId:D}',3,2,'{f.Contract:D}','{f.Invoice:D}',
                    {amount},'PAY-{paymentId:N}','2026-10-29T01:30:00Z',
                    'REF-{paymentId:N}',FALSE);
            """);
    }

    private static string AllocationSql(Fixture f, Guid paymentId, decimal amount)
        => $"""
           INSERT INTO "BuildingManagementInvoicePaymentAllocationJournal"
               ("Id","PaymentId","IssuedInvoiceId","ContractId","Amount",
                "PaymentNumberSnapshot","PaymentPaidAtUtc",
                "PaymentTransactionReferenceSnapshot",
                "RecordedByUserId","RecordedAtUtc")
           VALUES ('{Guid.NewGuid():D}','{paymentId:D}','{f.Invoice:D}',
                   '{f.Contract:D}',{amount},'PAY-{paymentId:N}',
                   '2026-10-29T01:30:00Z','REF-{paymentId:N}',
                   '{Guid.NewGuid():D}','2026-10-29T02:00:00Z');
           """;

    private static Task InsertAllocationAsync(
        NpgsqlConnection db, NpgsqlTransaction tx, Fixture f,
        Guid paymentId, decimal amount)
        => ExecuteAsync(db, tx, AllocationSql(f, paymentId, amount));

    private static string Sql(Migration migration, bool down)
    {
        var operations = down ? migration.DownOperations : migration.UpOperations;
        var sql = Assert.Single(operations.OfType<SqlOperation>());
        Assert.Single(operations);
        if (migration is SliceBInvoiceReconciliationBoundary)
            Assert.Contains(down ? "refuse rollback" : "FK_BM_Allocation_ActualIssuedInvoice",
                sql.Sql, StringComparison.Ordinal);
        return sql.Sql;
    }

    private static async Task InScratchAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, string, Task> test)
    {
        var prerequisite = new Migration[]
        {
            new SliceBFinalInvoiceEvidencePersistence(),
            new SliceBInvoicePaymentAllocationStaging(),
            new SliceBIssuedInvoiceLedger()
        };
        var up = Sql(new SliceBInvoiceReconciliationBoundary(), false);
        var down = Sql(new SliceBInvoiceReconciliationBoundary(), true);
        var allowed = BuildingManagementPostgreSqlTestModule.RequireSafeConnectionString();
        var opts = new NpgsqlConnectionStringBuilder(allowed)
        {
            Pooling = false,
            CommandTimeout = 30
        };
        await using var db = new NpgsqlConnection(opts.ConnectionString);
        await db.OpenAsync();
        Assert.Equal(BuildingManagementPostgreSqlTestModule.DatabaseName, db.Database);
        var schema = "bm_financial_chain_" + Guid.NewGuid().ToString("N");
        await using (var tx = await db.BeginTransactionAsync())
        {
            try
            {
                await ExecuteAsync(db, tx, $"CREATE SCHEMA \"{schema}\"");
                await ExecuteAsync(db, tx, $"SET LOCAL search_path TO \"{schema}\"");
                await ExecuteAsync(db, tx, "SET LOCAL lock_timeout = '3s'");
                await ExecuteAsync(db, tx, "SET LOCAL statement_timeout = '20s'");
                await ExecuteAsync(db, tx, """
                    CREATE TABLE "BuildingManagementInvoices" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "ContractId" uuid NOT NULL,
                        "RoomId" uuid NOT NULL,
                        "BillingYear" integer NOT NULL,
                        "BillingMonth" integer NOT NULL,
                        "Status" integer NOT NULL);
                    CREATE TABLE "BuildingManagementMoveOutRequests" (
                        "Id" uuid NOT NULL PRIMARY KEY);
                    CREATE TABLE "BuildingManagementInvoiceItems" (
                        "Id" uuid PRIMARY KEY, "InvoiceId" uuid NOT NULL
                        REFERENCES "BuildingManagementInvoices" ("Id"));
                    CREATE TABLE "BuildingManagementInvoiceItemMeterSources" (
                        "Id" uuid PRIMARY KEY, "InvoiceItemId" uuid NOT NULL
                        REFERENCES "BuildingManagementInvoiceItems" ("Id"));
                    CREATE TABLE "BuildingManagementPayments" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "Purpose" integer NOT NULL,
                        "Status" integer NOT NULL,
                        "ContractId" uuid NULL,
                        "InvoiceId" uuid NULL,
                        "Amount" numeric(18,0) NOT NULL,
                        "PaymentNumber" varchar(64) NOT NULL,
                        "PaidAt" timestamptz NULL,
                        "TransactionReference" varchar(128) NULL,
                        "IsDeleted" boolean NOT NULL DEFAULT FALSE);
                    """);
                foreach (var m in prerequisite)
                    await ExecuteAsync(db, tx, Sql(m, false));
                await ExecuteAsync(db, tx, up);
                await test(db, tx, down);
            }
            finally { await tx.RollbackAsync(); }
        }
        Assert.Equal(0L, await ScalarAsync(db, null,
            $"SELECT count(*) FROM pg_namespace WHERE nspname = '{schema}'"));
    }

    private static async Task ExpectFailureAsync(
        NpgsqlConnection db, NpgsqlTransaction tx,
        string sql, string expectedState)
    {
        const string sp = "bm_recon_savepoint";
        await ExecuteAsync(db, tx, $"SAVEPOINT {sp}");
        string? state = null;
        try { await ExecuteAsync(db, tx, sql); }
        catch (PostgresException error) { state = error.SqlState; }
        finally
        {
            await ExecuteAsync(db, tx, $"ROLLBACK TO SAVEPOINT {sp}");
            await ExecuteAsync(db, tx, $"RELEASE SAVEPOINT {sp}");
        }
        Assert.Equal(expectedState, state);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection db, NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(
        NpgsqlConnection db, NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
