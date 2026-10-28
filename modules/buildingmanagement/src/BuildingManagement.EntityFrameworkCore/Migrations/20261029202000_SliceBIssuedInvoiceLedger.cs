using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// SOURCE-ONLY issued invoice ledger. This defines the invariant boundary
/// for a future authorized posting transaction, but does not enable one.
/// No deployment or startup migration, no fee approval, no allocation writer.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029202000_SliceBIssuedInvoiceLedger")]
public sealed class SliceBIssuedInvoiceLedger : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE TABLE "BuildingManagementIssuedInvoiceLedger" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "DraftInvoiceId" uuid NOT NULL,
                "StagedEvidenceId" uuid NOT NULL,
                "ReviewTicketId" uuid NOT NULL,
                "MoveOutRequestId" uuid NOT NULL,
                "ContractId" uuid NOT NULL,
                "RoomId" uuid NOT NULL,
                "BuildingId" uuid NOT NULL,
                "BillingYear" integer NOT NULL,
                "BillingMonth" integer NOT NULL,
                "InvoiceNumber" varchar(64) NOT NULL,
                "IssuedByUserId" uuid NOT NULL,
                "IssuedAtUtc" timestamptz NOT NULL,
                "DueDate" date NOT NULL,
                "BaseAmount" numeric(18,3) NOT NULL,
                "AuxiliaryAmount" numeric(18,3) NOT NULL,
                "TotalAmount" numeric(18,3) NOT NULL,
                "EvidenceSha256" varchar(64) NOT NULL,
                "FinancialTermsJson" text NOT NULL,
                "FinancialTermsSha256" varchar(64) NOT NULL,
                CONSTRAINT "FK_BM_IssuedLedger_Draft"
                    FOREIGN KEY ("DraftInvoiceId")
                    REFERENCES "BuildingManagementInvoices" ("Id")
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_BM_IssuedLedger_Evidence"
                    FOREIGN KEY ("StagedEvidenceId")
                    REFERENCES "BuildingManagementFinalInvoiceIssueEvidence" ("Id")
                    ON DELETE RESTRICT,
                CONSTRAINT "FK_BM_IssuedLedger_Review"
                    FOREIGN KEY ("ReviewTicketId")
                    REFERENCES "BuildingManagementInvoiceIssuanceReviews" ("Id")
                    ON DELETE RESTRICT,
                CONSTRAINT "CK_BM_IssuedLedger_Amounts"
                    CHECK ("BaseAmount" > 0 AND "AuxiliaryAmount" >= 0
                        AND "TotalAmount" = "BaseAmount" + "AuxiliaryAmount"),
                CONSTRAINT "CK_BM_IssuedLedger_Period"
                    CHECK ("BillingYear" BETWEEN 2000 AND 2200
                       AND "BillingMonth" BETWEEN 1 AND 12),
                CONSTRAINT "CK_BM_IssuedLedger_Number"
                    CHECK (length(trim("InvoiceNumber")) BETWEEN 1 AND 64),
                CONSTRAINT "CK_BM_IssuedLedger_TermsJson"
                    CHECK (length("FinancialTermsJson") BETWEEN 1 AND 1000000
                       AND jsonb_typeof("FinancialTermsJson"::jsonb) = 'object'),
                CONSTRAINT "CK_BM_IssuedLedger_Hashes"
                    CHECK ("EvidenceSha256" ~ '^[0-9a-f]{64}$'
                       AND "FinancialTermsSha256" ~ '^[0-9a-f]{64}$'),
                CONSTRAINT "CK_BM_IssuedLedger_Ids"
                    CHECK ("Id" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "DraftInvoiceId" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "StagedEvidenceId" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "ReviewTicketId" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "MoveOutRequestId" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "ContractId" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "RoomId" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "BuildingId" <> '00000000-0000-0000-0000-000000000000'::uuid
                       AND "IssuedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid),
                CONSTRAINT "CK_BM_IssuedLedger_DueDate"
                    CHECK ("DueDate" >=
                        ("IssuedAtUtc" AT TIME ZONE 'Asia/Ho_Chi_Minh')::date)
            );
            CREATE UNIQUE INDEX "UX_BM_IssuedLedger_Number"
                ON "BuildingManagementIssuedInvoiceLedger" ("InvoiceNumber");
            CREATE UNIQUE INDEX "UX_BM_IssuedLedger_Draft"
                ON "BuildingManagementIssuedInvoiceLedger" ("DraftInvoiceId");
            CREATE UNIQUE INDEX "UX_BM_IssuedLedger_Evidence"
                ON "BuildingManagementIssuedInvoiceLedger" ("StagedEvidenceId");
            CREATE UNIQUE INDEX "UX_BM_IssuedLedger_Review"
                ON "BuildingManagementIssuedInvoiceLedger" ("ReviewTicketId");
            CREATE UNIQUE INDEX "UX_BM_IssuedLedger_Contract_Period"
                ON "BuildingManagementIssuedInvoiceLedger"
                ("ContractId","BillingYear","BillingMonth");
            CREATE UNIQUE INDEX "UX_BM_IssuedLedger_Room_Period"
                ON "BuildingManagementIssuedInvoiceLedger"
                ("RoomId","BillingYear","BillingMonth");

            CREATE FUNCTION "BM_GuardIssuedInvoiceLedger"()
            RETURNS trigger LANGUAGE plpgsql AS $ledger_guard$
            DECLARE archived record;
            DECLARE ticket record;
            DECLARE draft record;
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Issued invoice ledger is append-only'
                        USING ERRCODE = '23514';
                END IF;

                -- The draft is locked through commit to serialize
                -- issuance attempts with edits to its source header.
                SELECT * INTO draft FROM "BuildingManagementInvoices"
                  WHERE "Id" = NEW."DraftInvoiceId" FOR UPDATE;
                SELECT * INTO archived FROM "BuildingManagementFinalInvoiceIssueEvidence"
                  WHERE "Id" = NEW."StagedEvidenceId" FOR SHARE;
                SELECT * INTO ticket FROM "BuildingManagementInvoiceIssuanceReviews"
                  WHERE "Id" = NEW."ReviewTicketId" FOR SHARE;

                IF draft."Id" IS NULL OR archived."Id" IS NULL
                   OR ticket."Id" IS NULL
                   OR draft."Status" <> 0
                   OR draft."ContractId" IS DISTINCT FROM NEW."ContractId"
                   OR draft."RoomId" IS DISTINCT FROM NEW."RoomId"
                   OR draft."BillingYear" IS DISTINCT FROM NEW."BillingYear"
                   OR draft."BillingMonth" IS DISTINCT FROM NEW."BillingMonth"
                   OR archived."DraftInvoiceId" IS DISTINCT FROM NEW."DraftInvoiceId"
                   OR archived."MoveOutRequestId" IS DISTINCT FROM NEW."MoveOutRequestId"
                   OR archived."ContractId" IS DISTINCT FROM NEW."ContractId"
                   OR archived."RoomId" IS DISTINCT FROM NEW."RoomId"
                   OR archived."BillingYear" IS DISTINCT FROM NEW."BillingYear"
                   OR archived."BillingMonth" IS DISTINCT FROM NEW."BillingMonth"
                   OR archived."KnownBaseChargeTotal" IS DISTINCT FROM NEW."BaseAmount"
                   OR archived."EvidenceSha256" IS DISTINCT FROM NEW."EvidenceSha256"
                   OR ticket."StagedEvidenceId" IS DISTINCT FROM archived."Id"
                   OR ticket."DraftInvoiceId" IS DISTINCT FROM NEW."DraftInvoiceId"
                   OR ticket."MoveOutRequestId" IS DISTINCT FROM NEW."MoveOutRequestId"
                   OR ticket."ContractId" IS DISTINCT FROM NEW."ContractId"
                   OR ticket."RoomId" IS DISTINCT FROM NEW."RoomId"
                   OR ticket."BuildingId" IS DISTINCT FROM NEW."BuildingId"
                   OR ticket."EvidenceSha256" IS DISTINCT FROM NEW."EvidenceSha256"
                   OR ticket."Status" <> 2
                   OR ticket."ReviewedAtUtc" IS NULL
                   OR ticket."ReviewedAtUtc" > NEW."IssuedAtUtc"
                   OR ticket."ReviewedByUserId" IS NULL
                   OR ticket."ReviewedByUserId" = ticket."SubmittedByUserId"
                THEN
                    RAISE EXCEPTION 'Issued ledger source or review is inconsistent'
                        USING ERRCODE = '23514';
                END IF;
                -- The checked design review is not issuing authorization:
                -- independent authenticated permission, approved terms and
                -- transactional posting must still be implemented in writer.
                RETURN NEW;
            END;
            $ledger_guard$;

            CREATE TRIGGER "TR_BM_IssuedLedger_ImmutableRow"
                BEFORE INSERT OR UPDATE OR DELETE
                ON "BuildingManagementIssuedInvoiceLedger"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardIssuedInvoiceLedger"();
            CREATE TRIGGER "TR_BM_IssuedLedger_ImmutableTruncate"
                BEFORE TRUNCATE ON "BuildingManagementIssuedInvoiceLedger"
                FOR EACH STATEMENT EXECUTE FUNCTION "BM_GuardIssuedInvoiceLedger"();

            -- Even an otherwise valid UPDATE to an already-issued draft
            -- could rewrite billing history. Reject all header mutations.
            CREATE FUNCTION "BM_GuardIssuedDraftMutation"()
            RETURNS trigger LANGUAGE plpgsql AS $issued_draft_guard$
            BEGIN
                IF EXISTS (SELECT 1 FROM "BuildingManagementIssuedInvoiceLedger"
                           WHERE "DraftInvoiceId" = OLD."Id") THEN
                    RAISE EXCEPTION 'Draft linked to issued ledger is immutable'
                        USING ERRCODE = '23514';
                END IF;
                RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
            END;
            $issued_draft_guard$;
            CREATE TRIGGER "TR_BM_IssuedLedger_ProtectDraft"
                BEFORE UPDATE OR DELETE ON "BuildingManagementInvoices"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardIssuedDraftMutation"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementIssuedInvoiceLedger",
                       "BuildingManagementInvoices"
                IN ACCESS EXCLUSIVE MODE;
            DO $ledger_down$
            BEGIN
                IF EXISTS (SELECT 1 FROM "BuildingManagementIssuedInvoiceLedger" LIMIT 1) THEN
                    RAISE EXCEPTION 'Issued invoice history exists; refuse rollback'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $ledger_down$;
            DROP TRIGGER "TR_BM_IssuedLedger_ProtectDraft"
                ON "BuildingManagementInvoices";
            DROP TABLE "BuildingManagementIssuedInvoiceLedger";
            DROP FUNCTION "BM_GuardIssuedDraftMutation"();
            DROP FUNCTION "BM_GuardIssuedInvoiceLedger"();
            """);
    }
}
