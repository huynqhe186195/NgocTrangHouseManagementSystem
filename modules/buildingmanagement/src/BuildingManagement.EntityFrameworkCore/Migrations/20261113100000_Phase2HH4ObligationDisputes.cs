using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// H4-scoped dispute of an already committed deferred obligation. The
/// existing move-out dispute lane is bound to an official receivable posting
/// and a move-out request, so it cannot represent an owner-recorded H4
/// top-up/credit/refund dispute. This adds the same append-only,
/// independently-resolved audit shape keyed to the H4 obligation instead.
/// It never moves bank funds, never rewrites the original obligation.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261113100000_Phase2HH4ObligationDisputes")]
public sealed class Phase2HH4ObligationDisputes : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        CREATE TABLE "BuildingManagementRoomChangeH4ObligationDisputes"(
          "Id" uuid PRIMARY KEY,
          "ObligationId" uuid NOT NULL REFERENCES
            "BuildingManagementRoomChangeH4Obligations"("Id") ON DELETE RESTRICT,
          "ContractId" uuid NOT NULL REFERENCES
            "BuildingManagementContracts"("Id") ON DELETE RESTRICT,
          "BuildingId" uuid NOT NULL,
          "OpenedByUserId" uuid NOT NULL,
          "OpenedAtUtc" timestamptz NOT NULL,
          "Reason" varchar(500) NOT NULL,
          "EvidenceReference" varchar(500) NOT NULL,
          CONSTRAINT "CK_BM_H4DisputeShape" CHECK (
            "Id"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "ContractId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "BuildingId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "OpenedByUserId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND length(btrim("Reason")) BETWEEN 12 AND 500
            AND length(btrim("EvidenceReference")) BETWEEN 12 AND 500
          )
        );
        CREATE UNIQUE INDEX "UX_BM_H4Dispute_Obligation"
          ON "BuildingManagementRoomChangeH4ObligationDisputes"("ObligationId");

        CREATE TABLE "BuildingManagementRoomChangeH4ObligationDisputeResolutions"(
          "Id" uuid PRIMARY KEY,
          "DisputeId" uuid NOT NULL UNIQUE REFERENCES
            "BuildingManagementRoomChangeH4ObligationDisputes"("Id") ON DELETE RESTRICT,
          "ContractId" uuid NOT NULL REFERENCES
            "BuildingManagementContracts"("Id") ON DELETE RESTRICT,
          "OwnerId" uuid NOT NULL,
          "ResolvedAtUtc" timestamptz NOT NULL,
          "ObligationAccepted" boolean NOT NULL,
          "SignedOwnerResolutionReference" varchar(500) NOT NULL,
          CONSTRAINT "CK_BM_H4DisputeResolutionShape" CHECK (
            "Id"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "ContractId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "OwnerId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND length(btrim("SignedOwnerResolutionReference")) BETWEEN 12 AND 500
          )
        );

        CREATE FUNCTION "BM_GuardH4ObligationDispute"()
        RETURNS trigger LANGUAGE plpgsql AS $dispute$
        DECLARE obligation record;
        DECLARE original record;
        BEGIN
          IF TG_OP<>'INSERT' THEN
            RAISE EXCEPTION 'H4 dispute event is immutable'
              USING ERRCODE='23514';
          END IF;
          IF current_setting('transaction_isolation')<>'serializable' THEN
            RAISE EXCEPTION 'H4 dispute changes require serializable isolation'
              USING ERRCODE='23514';
          END IF;
          IF TG_TABLE_NAME='BuildingManagementRoomChangeH4ObligationDisputes' THEN
            SELECT * INTO obligation
              FROM "BuildingManagementRoomChangeH4Obligations"
             WHERE "Id"=NEW."ObligationId" FOR SHARE;
            IF obligation."Id" IS NULL
               OR NEW."ContractId" IS DISTINCT FROM obligation."TargetContractId"
               OR NEW."OpenedAtUtc">clock_timestamp()
               OR EXISTS (SELECT 1
                            FROM "BuildingManagementRoomChangeH4SettlementPostings"
                           WHERE "ObligationId"=NEW."ObligationId")
            THEN
              RAISE EXCEPTION
                'H4 dispute requires an open obligation with no settlement posting'
                USING ERRCODE='23514';
            END IF;
          ELSE
            SELECT * INTO original
              FROM "BuildingManagementRoomChangeH4ObligationDisputes"
             WHERE "Id"=NEW."DisputeId" AND "ContractId"=NEW."ContractId"
             FOR SHARE;
            IF original."Id" IS NULL
               OR NEW."ResolvedAtUtc">clock_timestamp()
            THEN
              RAISE EXCEPTION 'H4 dispute resolution must match its own dispute'
                USING ERRCODE='23514';
            END IF;
          END IF;
          RETURN NEW;
        END;
        $dispute$;

        CREATE FUNCTION "BM_DenyH4DisputeMutation"()
        RETURNS trigger LANGUAGE plpgsql AS $deny$
        BEGIN
          RAISE EXCEPTION 'H4 dispute evidence cannot be truncated'
            USING ERRCODE='23514';
        END;
        $deny$;

        CREATE TRIGGER "TR_BM_H4Dispute_Guard"
          BEFORE INSERT OR UPDATE OR DELETE
          ON "BuildingManagementRoomChangeH4ObligationDisputes"
          FOR EACH ROW EXECUTE FUNCTION "BM_GuardH4ObligationDispute"();
        CREATE TRIGGER "TR_BM_H4DisputeResolution_Guard"
          BEFORE INSERT OR UPDATE OR DELETE
          ON "BuildingManagementRoomChangeH4ObligationDisputeResolutions"
          FOR EACH ROW EXECUTE FUNCTION "BM_GuardH4ObligationDispute"();
        CREATE TRIGGER "TR_BM_H4Dispute_NoTruncate"
          BEFORE TRUNCATE ON "BuildingManagementRoomChangeH4ObligationDisputes"
          FOR EACH STATEMENT EXECUTE FUNCTION "BM_DenyH4DisputeMutation"();
        CREATE TRIGGER "TR_BM_H4DisputeResolution_NoTruncate"
          BEFORE TRUNCATE
          ON "BuildingManagementRoomChangeH4ObligationDisputeResolutions"
          FOR EACH STATEMENT EXECUTE FUNCTION "BM_DenyH4DisputeMutation"();
        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        LOCK TABLE "BuildingManagementRoomChangeH4ObligationDisputes",
                   "BuildingManagementRoomChangeH4ObligationDisputeResolutions"
          IN ACCESS EXCLUSIVE MODE;
        DO $retained$
        BEGIN
          IF EXISTS(SELECT 1 FROM
              "BuildingManagementRoomChangeH4ObligationDisputeResolutions" LIMIT 1)
             OR EXISTS(SELECT 1 FROM
              "BuildingManagementRoomChangeH4ObligationDisputes" LIMIT 1) THEN
            RAISE EXCEPTION 'Cannot drop signed H4 dispute evidence'
              USING ERRCODE='23514';
          END IF;
        END;
        $retained$;
        DROP TABLE "BuildingManagementRoomChangeH4ObligationDisputeResolutions";
        DROP TABLE "BuildingManagementRoomChangeH4ObligationDisputes";
        DROP FUNCTION "BM_GuardH4ObligationDispute"();
        DROP FUNCTION "BM_DenyH4DisputeMutation"();
        """);
}
