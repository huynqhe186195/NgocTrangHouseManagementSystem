using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Independent append-only 2H D01/D03/D04/D06 evidence archives.
/// No financial ledger posting or mid-month writer. Production excluded.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261103080000_Phase2HDocumentaryEvidenceArchive")]
public sealed class Phase2HDocumentaryEvidenceArchive : Migration
{
    protected override void Up(MigrationBuilder builder) => builder.Sql("""
        CREATE TABLE "BuildingManagementRoomChangeEvidenceArchives" (
         "Id" uuid PRIMARY KEY,
         "RequestId" uuid NOT NULL REFERENCES "BuildingManagementRoomChangeRequests"("Id") ON DELETE RESTRICT,
         "SourceContractId" uuid NOT NULL REFERENCES "BuildingManagementContracts"("Id") ON DELETE RESTRICT,
         "BuildingId" uuid NOT NULL,
         "Kind" integer NOT NULL,
         "EvidenceId" uuid NOT NULL,
         "SourceEvidenceSha256" varchar(64) NOT NULL,
         "ParentEvidenceId" uuid NOT NULL,
         "ParentEvidenceSha256" varchar(64) NOT NULL,
         "EvidenceJson" text NOT NULL,
         "EvidenceJsonSha256" varchar(64) NOT NULL,
         "ArchivedByUserId" uuid NOT NULL,
         "ArchivedAtUtc" timestamptz NOT NULL,
         "RecordSha256" varchar(64) NOT NULL,
         CONSTRAINT "CK_BM_2HArchive_Valid" CHECK (
           "Id" <> '00000000-0000-0000-0000-000000000000'::uuid
           AND "RequestId" <> '00000000-0000-0000-0000-000000000000'::uuid
           AND "SourceContractId" <> '00000000-0000-0000-0000-000000000000'::uuid
           AND "BuildingId" <> '00000000-0000-0000-0000-000000000000'::uuid
           AND "EvidenceId" <> '00000000-0000-0000-0000-000000000000'::uuid
           AND "ParentEvidenceId" <> '00000000-0000-0000-0000-000000000000'::uuid
           AND "ArchivedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid
           AND "Kind" IN (1,2,3,4)
           AND length("EvidenceJson") BETWEEN 10 AND 200000
           AND "SourceEvidenceSha256" ~ '^[0-9a-f]{64}$'
           AND "ParentEvidenceSha256" ~ '^[0-9a-f]{64}$'
           AND "EvidenceJsonSha256" ~ '^[0-9a-f]{64}$'
           AND "RecordSha256" ~ '^[0-9a-f]{64}$'
         )
        );
        CREATE UNIQUE INDEX "UX_BM_2HArchive_KindEvidence"
          ON "BuildingManagementRoomChangeEvidenceArchives"("Kind","EvidenceId");
        CREATE UNIQUE INDEX "UX_BM_2HArchive_RequestParent"
          ON "BuildingManagementRoomChangeEvidenceArchives"("RequestId","Kind","ParentEvidenceId")
          WHERE "Kind" IN (1,3,4);
        CREATE UNIQUE INDEX "UX_BM_2HArchive_OneSignoffPerRequest"
          ON "BuildingManagementRoomChangeEvidenceArchives"("RequestId","Kind")
          WHERE "Kind" IN (1,3,4);
        
        CREATE FUNCTION "BM_CalculateRoomChangeEvidenceArchiveSha"(
         archive "BuildingManagementRoomChangeEvidenceArchives"
        ) RETURNS text LANGUAGE sql AS $sha$
         SELECT encode(sha256(convert_to(concat_ws(E'\n',
          archive."Id"::text, archive."RequestId"::text,
          archive."SourceContractId"::text, archive."BuildingId"::text,
          archive."Kind"::text, archive."EvidenceId"::text,
          archive."SourceEvidenceSha256",
          archive."ParentEvidenceId"::text,
          archive."ParentEvidenceSha256", archive."EvidenceJsonSha256",
          archive."ArchivedByUserId"::text,
          to_char(archive."ArchivedAtUtc" AT TIME ZONE 'UTC',
            'YYYY-MM-DD"T"HH24:MI:SS.US')||'0Z'
         ),'UTF8')),'hex');
        $sha$;
        
        CREATE FUNCTION "BM_GuardRoomChangeEvidenceArchive"()
        RETURNS trigger LANGUAGE plpgsql AS $guard$
        DECLARE req record; original_sha text; owner_reviewer uuid;
                proof jsonb; id_field text; sha_field text;
        BEGIN
         IF TG_OP <> 'INSERT' THEN
          RAISE EXCEPTION '2H evidence archive is append-only'
            USING ERRCODE='23514';
         END IF;
         IF current_setting('transaction_isolation') <> 'serializable' THEN
          RAISE EXCEPTION '2H evidence archive requires SERIALIZABLE'
            USING ERRCODE='23514';
         END IF;
         SELECT * INTO req FROM "BuildingManagementRoomChangeRequests"
          WHERE "Id"=NEW."RequestId" FOR SHARE;
        
         IF req."Id" IS NULL OR req."Status" NOT IN (0,1)
            OR req."SourceContractId" IS DISTINCT FROM NEW."SourceContractId"
            OR req."BuildingId" IS DISTINCT FROM NEW."BuildingId"
            OR NEW."ArchivedAtUtc" < req."CreatedAtUtc"
            OR NEW."EvidenceJsonSha256" IS DISTINCT FROM
               encode(sha256(convert_to(NEW."EvidenceJson",'UTF8')),'hex')
            OR NEW."RecordSha256" IS DISTINCT FROM
               "BM_CalculateRoomChangeEvidenceArchiveSha"(NEW)
         THEN
          RAISE EXCEPTION '2H evidence archive scope or canonical SHA mismatch'
            USING ERRCODE='23514';
         END IF;
        
         proof := NEW."EvidenceJson"::jsonb;
         IF proof->>'Id' IS DISTINCT FROM NEW."EvidenceId"::text
            OR proof->>'EvidenceSha256' IS DISTINCT FROM NEW."SourceEvidenceSha256"
         THEN
          RAISE EXCEPTION '2H evidence archive payload source ID/SHA differs'
            USING ERRCODE='23514';
         END IF;
        
         CASE NEW."Kind"
         WHEN 1 THEN
          id_field := 'PricingRuleId'; sha_field := 'PricingRuleSha256';
          SELECT "EvidenceSha256" INTO original_sha
           FROM "BuildingManagementRoomChangeDayPricingRuleCandidates"
           WHERE "Id"=NEW."ParentEvidenceId"
             AND "RequestId"=NEW."RequestId"
             AND "SourceContractId"=NEW."SourceContractId"
             AND "BuildingId"=NEW."BuildingId" FOR SHARE;
          SELECT rv."ReviewedByUserId" INTO owner_reviewer
           FROM "BuildingManagementRoomChangeDayPricingRuleCandidates" dr
           JOIN "BuildingManagementRoomChangeBusinessPolicyReviews" rv
             ON rv."Id"=dr."PolicyReviewId"
           WHERE dr."Id"=NEW."ParentEvidenceId";
          IF proof->>'RequestId' IS DISTINCT FROM NEW."RequestId"::text
             OR proof->>'OwnerUserId' IS DISTINCT FROM NEW."ArchivedByUserId"::text
             OR NEW."ArchivedByUserId" IS DISTINCT FROM owner_reviewer
          THEN
           RAISE EXCEPTION 'D01/D02 approval differs from original independent reviewer'
             USING ERRCODE='23514';
          END IF;
         WHEN 2 THEN
          id_field := 'IssuedInvoiceId'; sha_field := 'OriginalInvoiceSha256';
          SELECT "CandidateSha256" INTO original_sha
            FROM "BuildingManagementMonthlyIssuedInvoiceLedger"
           WHERE "Id"=NEW."ParentEvidenceId"
             AND "ContractId"=NEW."SourceContractId"
             AND "BuildingId"=NEW."BuildingId"
             AND "RoomId"=req."SourceRoomId"
             AND "BillingYear"=EXTRACT(year FROM req."RequestedDate")
             AND "BillingMonth"=EXTRACT(month FROM req."RequestedDate")
           FOR SHARE;
          IF proof->>'ContractId' IS DISTINCT FROM NEW."SourceContractId"::text
             OR proof->>'BuildingId' IS DISTINCT FROM NEW."BuildingId"::text
             OR proof->>'ClassifiedByUserId' IS DISTINCT FROM NEW."ArchivedByUserId"::text
          THEN
           RAISE EXCEPTION 'D03 correction differs from source invoice'
             USING ERRCODE='23514';
          END IF;
         WHEN 3 THEN
          id_field := 'FinancialPacketId'; sha_field := 'FinancialPacketSha256';
          -- The packet is not yet persisted. This protects the archived signed
          -- candidate, NOT the execution, settlement, or bank authenticity.
          original_sha := NEW."ParentEvidenceSha256";
          IF proof->>'RequestId' IS DISTINCT FROM NEW."RequestId"::text
             OR proof->>'SourceContractId' IS DISTINCT FROM NEW."SourceContractId"::text
             OR proof->>'IndependentBankReviewerId' IS DISTINCT FROM
               NEW."ArchivedByUserId"::text
             OR proof->>'TargetContractId' IS NULL
             OR proof->>'TargetContractId'=NEW."SourceContractId"::text
          THEN
           RAISE EXCEPTION 'D04 manual cash attestation scope mismatch'
             USING ERRCODE='23514';
          END IF;
         WHEN 4 THEN
          id_field := 'HandoverId'; sha_field := 'PhysicalHandoverSha256';
          SELECT "EvidenceSha256" INTO original_sha
            FROM "BuildingManagementRoomChangeHandoverEvidence"
           WHERE "Id"=NEW."ParentEvidenceId"
             AND "RequestId"=NEW."RequestId"
             AND "SourceContractId"=NEW."SourceContractId"
             AND "BuildingId"=NEW."BuildingId"
           FOR SHARE;
          IF proof->>'RequestId' IS DISTINCT FROM NEW."RequestId"::text
             OR proof->>'OwnerSignerId' IS DISTINCT FROM NEW."ArchivedByUserId"::text
          THEN
           RAISE EXCEPTION 'D06 physical signoff scope mismatch'
             USING ERRCODE='23514';
          END IF;
         ELSE
          RAISE EXCEPTION 'Unknown 2H documentary evidence'
            USING ERRCODE='23514';
         END CASE;
        
         IF original_sha IS NULL
            OR original_sha IS DISTINCT FROM NEW."ParentEvidenceSha256"
            OR proof->>id_field IS DISTINCT FROM NEW."ParentEvidenceId"::text
            OR proof->>sha_field IS DISTINCT FROM NEW."ParentEvidenceSha256"
         THEN
          RAISE EXCEPTION '2H archive parent evidence cannot be independently matched'
            USING ERRCODE='23514';
         END IF;
         RETURN NEW;
        END;
        $guard$;
        CREATE TRIGGER "TR_BM_2HArchive_ImmutableIntegrity"
         BEFORE INSERT OR UPDATE OR DELETE
         ON "BuildingManagementRoomChangeEvidenceArchives"
         FOR EACH ROW EXECUTE FUNCTION "BM_GuardRoomChangeEvidenceArchive"();
        
        CREATE FUNCTION "BM_2HArchiveNoTruncate"()
         RETURNS trigger LANGUAGE plpgsql AS $deny$
        BEGIN
         RAISE EXCEPTION '2H documentary evidence cannot be truncated'
          USING ERRCODE='23514';
        END;
        $deny$;
        CREATE TRIGGER "TR_BM_2HArchive_NoTruncate"
         BEFORE TRUNCATE ON "BuildingManagementRoomChangeEvidenceArchives"
         FOR EACH STATEMENT EXECUTE FUNCTION "BM_2HArchiveNoTruncate"();
        """);

    protected override void Down(MigrationBuilder builder) => builder.Sql("""
        LOCK TABLE "BuildingManagementRoomChangeEvidenceArchives" IN ACCESS EXCLUSIVE MODE;
        DO $guard$
        BEGIN
         IF EXISTS (SELECT 1 FROM "BuildingManagementRoomChangeEvidenceArchives" LIMIT 1)
         THEN
           RAISE EXCEPTION 'Cannot remove signed historical 2H documentary evidence'
            USING ERRCODE='23514';
         END IF;
        END;
        $guard$;
        DROP TRIGGER "TR_BM_2HArchive_NoTruncate"
         ON "BuildingManagementRoomChangeEvidenceArchives";
        DROP FUNCTION "BM_2HArchiveNoTruncate"();
        DROP TRIGGER "TR_BM_2HArchive_ImmutableIntegrity"
         ON "BuildingManagementRoomChangeEvidenceArchives";
        DROP FUNCTION "BM_GuardRoomChangeEvidenceArchive"();
        DROP FUNCTION "BM_CalculateRoomChangeEvidenceArchiveSha"(
         "BuildingManagementRoomChangeEvidenceArchives");
        DROP TABLE "BuildingManagementRoomChangeEvidenceArchives";
        """);
}
