using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Source-only evidence table for monthly rent + electricity + water and
/// explicitly assessed contracted service fees. NO recognized invoice,
/// customer debt, payment endpoint, production rollout or implicit charges.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029220000_Phase2G3MonthlyCandidateEvidence")]
public sealed class Phase2G3MonthlyCandidateEvidence : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE TABLE "BuildingManagementMonthlyInvoiceCandidates" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "DraftInvoiceId" uuid NOT NULL,
                "ContractId" uuid NOT NULL,
                "RoomId" uuid NOT NULL,
                "BuildingId" uuid NOT NULL,
                "BillingYear" integer NOT NULL,
                "BillingMonth" integer NOT NULL,
                "ReviewedByUserId" uuid NOT NULL,
                "ReviewedAtUtc" timestamptz NOT NULL,
                "DueDate" date NOT NULL,
                "RentAmount" numeric(18,0) NOT NULL,
                "ElectricityAmount" numeric(18,0) NOT NULL,
                "WaterAmount" numeric(18,0) NOT NULL,
                "AuxiliaryAmount" numeric(18,0) NOT NULL,
                "TotalAmount" numeric(18,0) NOT NULL,
                "FrozenEvidenceJson" text NOT NULL,
                "FrozenEvidenceSha256" varchar(64) NOT NULL,
                CONSTRAINT "FK_BM_MonthlyCandidate_Draft" FOREIGN KEY ("DraftInvoiceId")
                    REFERENCES "BuildingManagementInvoices" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "CK_BM_MonthlyCandidate_Amounts"
                    CHECK ("RentAmount" > 0 AND "ElectricityAmount" >= 0 AND
                           "WaterAmount" >= 0 AND "AuxiliaryAmount" >= 0 AND
                           "TotalAmount" = "RentAmount" + "ElectricityAmount" +
                               "WaterAmount" + "AuxiliaryAmount"),
                CONSTRAINT "CK_BM_MonthlyCandidate_Period"
                    CHECK ("BillingYear" BETWEEN 2000 AND 2200 AND
                           "BillingMonth" BETWEEN 1 AND 12),
                CONSTRAINT "CK_BM_MonthlyCandidate_Due"
                    CHECK ("DueDate" >= ("ReviewedAtUtc" AT TIME ZONE
                        'Asia/Ho_Chi_Minh')::date),
                CONSTRAINT "CK_BM_MonthlyCandidate_Snapshot"
                    CHECK (length("FrozenEvidenceJson") BETWEEN 10 AND 1000000
                           AND "FrozenEvidenceSha256" ~ '^[0-9a-f]{64}$'
                           AND "FrozenEvidenceJson"::jsonb->>'DraftInvoiceId' =
                               "DraftInvoiceId"::text
                           AND "FrozenEvidenceJson"::jsonb->>'ContractId' =
                               "ContractId"::text
                           AND "FrozenEvidenceJson"::jsonb->>'RoomId' =
                               "RoomId"::text
                           AND "FrozenEvidenceJson"::jsonb->>'BuildingId' =
                               "BuildingId"::text
                           AND ("FrozenEvidenceJson"::jsonb->>'BillingYear')::integer =
                               "BillingYear"
                           AND ("FrozenEvidenceJson"::jsonb->>'BillingMonth')::integer =
                               "BillingMonth"
                           AND "FrozenEvidenceJson"::jsonb->>'ReviewedByUserId' =
                               "ReviewedByUserId"::text
                           AND ("FrozenEvidenceJson"::jsonb->>'RentAmount')::numeric =
                               "RentAmount"
                           AND ("FrozenEvidenceJson"::jsonb->>'ElectricityAmount')::numeric =
                               "ElectricityAmount"
                           AND ("FrozenEvidenceJson"::jsonb->>'WaterAmount')::numeric =
                               "WaterAmount"
                           AND ("FrozenEvidenceJson"::jsonb->>'AuxiliaryAmount')::numeric =
                               "AuxiliaryAmount"
                           AND ("FrozenEvidenceJson"::jsonb->>'TotalAmount')::numeric =
                               "TotalAmount"
                           AND ("FrozenEvidenceJson"::jsonb->>'DueDate')::date =
                               "DueDate")
            );
            CREATE UNIQUE INDEX "UX_BM_MonthlyCandidate_Draft"
                ON "BuildingManagementMonthlyInvoiceCandidates" ("DraftInvoiceId");
            CREATE UNIQUE INDEX "UX_BM_MonthlyCandidate_ContractPeriod"
                ON "BuildingManagementMonthlyInvoiceCandidates"
                    ("ContractId", "BillingYear", "BillingMonth");
            CREATE UNIQUE INDEX "UX_BM_MonthlyCandidate_RoomPeriod"
                ON "BuildingManagementMonthlyInvoiceCandidates"
                    ("RoomId", "BillingYear", "BillingMonth");

            CREATE FUNCTION "BM_GuardMonthlyCandidateHistory"()
            RETURNS trigger LANGUAGE plpgsql AS $monthly_guard$
            DECLARE persisted_draft record;
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Monthly candidate evidence is append-only'
                        USING ERRCODE = '23514';
                END IF;
                -- Use the original draft lock to serialize simultaneous
                -- issuance/correction requests for this room-month.
                SELECT * INTO persisted_draft FROM "BuildingManagementInvoices"
                 WHERE "Id" = NEW."DraftInvoiceId" FOR UPDATE;
                IF NOT FOUND OR persisted_draft."ContractId" != NEW."ContractId"
                   OR persisted_draft."RoomId" != NEW."RoomId"
                   OR persisted_draft."BillingYear" != NEW."BillingYear"
                   OR persisted_draft."BillingMonth" != NEW."BillingMonth"
                   OR persisted_draft."Status" != 0 THEN
                    RAISE EXCEPTION 'Monthly candidate must match one active draft'
                        USING ERRCODE = '23514';
                END IF;
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementIssuedInvoiceLedger" i
                     WHERE (i."ContractId" = NEW."ContractId" OR
                            i."RoomId" = NEW."RoomId")
                       AND i."BillingYear" = NEW."BillingYear"
                       AND i."BillingMonth" = NEW."BillingMonth"
                ) THEN
                    RAISE EXCEPTION 'Room-month already has final issued invoice'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $monthly_guard$;
            CREATE TRIGGER "TR_BM_MonthlyCandidate_Immutable"
                BEFORE INSERT OR UPDATE OR DELETE
                ON "BuildingManagementMonthlyInvoiceCandidates"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardMonthlyCandidateHistory"();

            CREATE FUNCTION "BM_GuardMonthlyCandidateTruncate"()
            RETURNS trigger LANGUAGE plpgsql AS $monthly_truncate$
            BEGIN
                RAISE EXCEPTION 'Monthly candidate evidence cannot be truncated'
                    USING ERRCODE = '23514';
            END;
            $monthly_truncate$;
            CREATE TRIGGER "TR_BM_MonthlyCandidate_NoTruncate"
                BEFORE TRUNCATE ON "BuildingManagementMonthlyInvoiceCandidates"
                FOR EACH STATEMENT EXECUTE FUNCTION "BM_GuardMonthlyCandidateTruncate"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementMonthlyInvoiceCandidates"
                IN ACCESS EXCLUSIVE MODE;
            DO $monthly_down$
            BEGIN
                IF EXISTS (SELECT 1 FROM "BuildingManagementMonthlyInvoiceCandidates" LIMIT 1)
                THEN
                    RAISE EXCEPTION 'Monthly candidate history exists; refuse irreversible rollback'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $monthly_down$;
            DROP TRIGGER "TR_BM_MonthlyCandidate_NoTruncate"
                ON "BuildingManagementMonthlyInvoiceCandidates";
            DROP FUNCTION "BM_GuardMonthlyCandidateTruncate"();
            DROP TRIGGER "TR_BM_MonthlyCandidate_Immutable"
                ON "BuildingManagementMonthlyInvoiceCandidates";
            DROP FUNCTION "BM_GuardMonthlyCandidateHistory"();
            DROP TABLE "BuildingManagementMonthlyInvoiceCandidates";
            """);
    }
}
