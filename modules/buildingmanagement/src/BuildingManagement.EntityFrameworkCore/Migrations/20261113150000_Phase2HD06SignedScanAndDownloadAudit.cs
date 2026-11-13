using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// D06 closure. Two append-only trails:
/// 1. the scanned signed handover sheet the Owner's 2026-11-04 decision
///    accepts as D06 evidence (cryptographic e-signature explicitly not
///    required), bound to an already archived three-party handover;
/// 2. the Owner contract document download trail.
/// Neither moves money, proves signer identity, or executes a transfer.
/// Only digests and opaque vault handles are stored; never raw documents.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261113150000_Phase2HD06SignedScanAndDownloadAudit")]
public sealed class Phase2HD06SignedScanAndDownloadAudit : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        CREATE TABLE "BuildingManagementRoomChangeSignedHandoverScans"(
          "Id" uuid PRIMARY KEY,
          "RequestId" uuid NOT NULL REFERENCES
            "BuildingManagementRoomChangeRequests"("Id") ON DELETE RESTRICT,
          "SourceContractId" uuid NOT NULL REFERENCES
            "BuildingManagementContracts"("Id") ON DELETE RESTRICT,
          "BuildingId" uuid NOT NULL,
          "HandoverArchiveId" uuid NOT NULL REFERENCES
            "BuildingManagementRoomChangeEvidenceArchives"("Id") ON DELETE RESTRICT,
          "HandoverEvidenceSha256" varchar(64) NOT NULL,
          "HandoverDocumentSha256" varchar(64) NOT NULL,
          "MediaType" varchar(64) NOT NULL,
          "ByteLength" integer NOT NULL,
          "VaultReference" varchar(32) NOT NULL,
          "UploadedByUserId" uuid NOT NULL,
          "UploadedAtUtc" timestamptz NOT NULL,
          "EvidenceSha256" varchar(64) NOT NULL,
          CONSTRAINT "CK_BM_D06ScanShape" CHECK (
            "Id"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "RequestId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "SourceContractId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "BuildingId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "HandoverArchiveId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "UploadedByUserId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "HandoverEvidenceSha256" ~ '^[0-9a-f]{64}$'
            AND "HandoverDocumentSha256" ~ '^[0-9a-f]{64}$'
            AND "EvidenceSha256" ~ '^[0-9a-f]{64}$'
            AND "MediaType" IN ('application/pdf','image/png','image/jpeg')
            AND "ByteLength" BETWEEN 256 AND 5242880
            AND "VaultReference" ~ '^[0-9a-f]{32}$'
          )
        );
        CREATE UNIQUE INDEX "UX_BM_D06Scan_Handover"
          ON "BuildingManagementRoomChangeSignedHandoverScans"("HandoverArchiveId");
        CREATE UNIQUE INDEX "UX_BM_D06Scan_Request"
          ON "BuildingManagementRoomChangeSignedHandoverScans"("RequestId");

        CREATE TABLE "BuildingManagementContractDocumentAccess"(
          "Id" uuid PRIMARY KEY,
          "ContractId" uuid NOT NULL REFERENCES
            "BuildingManagementContracts"("Id") ON DELETE RESTRICT,
          "BuildingId" uuid NOT NULL,
          "AccessedByUserId" uuid NOT NULL,
          "AccessedAtUtc" timestamptz NOT NULL,
          "Purpose" varchar(300) NOT NULL,
          "DeliveredDocumentSha256" varchar(64) NOT NULL,
          "DeliveredByteLength" integer NOT NULL,
          "EvidenceSha256" varchar(64) NOT NULL,
          CONSTRAINT "CK_BM_DocAccessShape" CHECK (
            "Id"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "ContractId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "BuildingId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "AccessedByUserId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND length(btrim("Purpose")) BETWEEN 12 AND 300
            AND "DeliveredDocumentSha256" ~ '^[0-9a-f]{64}$'
            AND "EvidenceSha256" ~ '^[0-9a-f]{64}$'
            AND "DeliveredByteLength" BETWEEN 1024 AND 5242880
          )
        );
        CREATE INDEX "IX_BM_DocAccess_Contract"
          ON "BuildingManagementContractDocumentAccess"("ContractId","AccessedAtUtc");

        CREATE FUNCTION "BM_GuardD06SignedHandoverScan"()
        RETURNS trigger LANGUAGE plpgsql AS $scan$
        DECLARE archive record;
        BEGIN
          IF TG_OP<>'INSERT' THEN
            RAISE EXCEPTION 'D06 signed scan is append-only'
              USING ERRCODE='23514';
          END IF;
          IF current_setting('transaction_isolation')<>'serializable' THEN
            RAISE EXCEPTION 'D06 signed scan requires serializable isolation'
              USING ERRCODE='23514';
          END IF;
          SELECT * INTO archive
            FROM "BuildingManagementRoomChangeEvidenceArchives"
           WHERE "Id"=NEW."HandoverArchiveId" FOR SHARE;
          IF archive."Id" IS NULL
             OR archive."Kind"<>4
             OR archive."RequestId" IS DISTINCT FROM NEW."RequestId"
             OR archive."SourceContractId" IS DISTINCT FROM NEW."SourceContractId"
             OR archive."BuildingId" IS DISTINCT FROM NEW."BuildingId"
             OR archive."RecordSha256" IS DISTINCT FROM NEW."HandoverEvidenceSha256"
             OR NEW."UploadedAtUtc">clock_timestamp()
             OR NEW."UploadedAtUtc"<archive."ArchivedAtUtc"
          THEN
            RAISE EXCEPTION
              'D06 scan requires the archived three-party handover of the same request'
              USING ERRCODE='23514';
          END IF;
          RETURN NEW;
        END;
        $scan$;

        CREATE FUNCTION "BM_GuardContractDocumentAccess"()
        RETURNS trigger LANGUAGE plpgsql AS $access$
        DECLARE contract record;
        BEGIN
          IF TG_OP<>'INSERT' THEN
            RAISE EXCEPTION 'Contract document access trail is append-only'
              USING ERRCODE='23514';
          END IF;
          IF current_setting('transaction_isolation')<>'serializable'
             AND current_setting('transaction_isolation')<>'repeatable read' THEN
            RAISE EXCEPTION 'Contract document access needs a stable snapshot'
              USING ERRCODE='23514';
          END IF;
          SELECT c."Id" AS id INTO contract
            FROM "BuildingManagementContracts" c
           WHERE c."Id"=NEW."ContractId" AND c."IsDeleted"=FALSE
           FOR SHARE;
          IF contract."id" IS NULL
             OR NEW."AccessedAtUtc">clock_timestamp()
          THEN
            RAISE EXCEPTION 'Contract document access must reference a live contract'
              USING ERRCODE='23514';
          END IF;
          RETURN NEW;
        END;
        $access$;

        CREATE FUNCTION "BM_DenyD06ScanMutation"()
        RETURNS trigger LANGUAGE plpgsql AS $deny$
        BEGIN
          RAISE EXCEPTION 'D06 signed scan evidence cannot be truncated'
            USING ERRCODE='23514';
        END;
        $deny$;

        CREATE FUNCTION "BM_DenyContractDocumentAccessMutation"()
        RETURNS trigger LANGUAGE plpgsql AS $deny$
        BEGIN
          RAISE EXCEPTION 'Contract document access trail cannot be truncated'
            USING ERRCODE='23514';
        END;
        $deny$;

        CREATE TRIGGER "TR_BM_D06Scan_Guard"
          BEFORE INSERT OR UPDATE OR DELETE
          ON "BuildingManagementRoomChangeSignedHandoverScans"
          FOR EACH ROW EXECUTE FUNCTION "BM_GuardD06SignedHandoverScan"();
        CREATE TRIGGER "TR_BM_D06Scan_NoTruncate"
          BEFORE TRUNCATE ON "BuildingManagementRoomChangeSignedHandoverScans"
          FOR EACH STATEMENT EXECUTE FUNCTION "BM_DenyD06ScanMutation"();
        CREATE TRIGGER "TR_BM_DocAccess_Guard"
          BEFORE INSERT OR UPDATE OR DELETE
          ON "BuildingManagementContractDocumentAccess"
          FOR EACH ROW EXECUTE FUNCTION "BM_GuardContractDocumentAccess"();
        CREATE TRIGGER "TR_BM_DocAccess_NoTruncate"
          BEFORE TRUNCATE ON "BuildingManagementContractDocumentAccess"
          FOR EACH STATEMENT EXECUTE FUNCTION "BM_DenyContractDocumentAccessMutation"();
        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        LOCK TABLE "BuildingManagementRoomChangeSignedHandoverScans",
                   "BuildingManagementContractDocumentAccess"
          IN ACCESS EXCLUSIVE MODE;
        DO $retained$
        BEGIN
          IF EXISTS(SELECT 1 FROM
              "BuildingManagementRoomChangeSignedHandoverScans" LIMIT 1)
             OR EXISTS(SELECT 1 FROM
              "BuildingManagementContractDocumentAccess" LIMIT 1) THEN
            RAISE EXCEPTION 'Cannot drop signed D06 scan or download audit evidence'
              USING ERRCODE='23514';
          END IF;
        END;
        $retained$;
        DROP TABLE "BuildingManagementContractDocumentAccess";
        DROP TABLE "BuildingManagementRoomChangeSignedHandoverScans";
        DROP FUNCTION "BM_GuardD06SignedHandoverScan"();
        DROP FUNCTION "BM_GuardContractDocumentAccess"();
        DROP FUNCTION "BM_DenyD06ScanMutation"();
        DROP FUNCTION "BM_DenyContractDocumentAccessMutation"();
        """);
}
