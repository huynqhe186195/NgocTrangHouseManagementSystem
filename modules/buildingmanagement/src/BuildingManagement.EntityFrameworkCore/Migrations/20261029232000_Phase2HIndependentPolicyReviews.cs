using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Root-reviewed OI-07 evidence is append-only and is not a transfer event.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029232000_Phase2HIndependentPolicyReviews")]
public sealed class Phase2HIndependentPolicyReviews : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE TABLE "BuildingManagementRoomChangeBusinessPolicyReviews" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "PolicyId" uuid NOT NULL
                    REFERENCES "BuildingManagementRoomChangeBusinessPolicies" ("Id")
                    ON DELETE RESTRICT,
                "RequestId" uuid NOT NULL,
                "BuildingId" uuid NOT NULL,
                "ProposalSha256" varchar(64) NOT NULL,
                "ReviewedByUserId" uuid NOT NULL,
                "ReviewedAtUtc" timestamptz NOT NULL,
                "Accepted" boolean NOT NULL,
                "SignedReviewReference" varchar(500) NOT NULL,
                "Reason" varchar(500) NOT NULL,
                CONSTRAINT "CK_BM_RoomChangeReview_Valid" CHECK (
                    "Id" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "ReviewedByUserId" <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND "ProposalSha256" ~ '^[0-9a-f]{64}$'
                    AND length(btrim("SignedReviewReference")) BETWEEN 12 AND 500
                    AND length(btrim("Reason")) BETWEEN 12 AND 500
                )
            );
            CREATE UNIQUE INDEX "UX_BM_RoomChangePolicyReview_Policy"
              ON "BuildingManagementRoomChangeBusinessPolicyReviews" ("PolicyId");

            CREATE FUNCTION "BM_GuardRoomChangePolicyReview"()
            RETURNS trigger LANGUAGE plpgsql AS $review$
            DECLARE policy record;
            DECLARE request record;
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'OI-07 independent review cannot be rewritten'
                        USING ERRCODE = '23514';
                END IF;
                IF current_setting('transaction_isolation') <> 'serializable' THEN
                    RAISE EXCEPTION 'OI-07 review requires serializable transaction'
                        USING ERRCODE = '23514';
                END IF;
                SELECT * INTO policy
                  FROM "BuildingManagementRoomChangeBusinessPolicies"
                 WHERE "Id" = NEW."PolicyId" FOR SHARE;
                SELECT * INTO request
                  FROM "BuildingManagementRoomChangeRequests"
                 WHERE "Id" = NEW."RequestId" FOR SHARE;
                IF policy."Id" IS NULL OR request."Id" IS NULL
                   OR request."Status" NOT IN (0,1)
                   OR policy."RequestId" IS DISTINCT FROM NEW."RequestId"
                   OR policy."BuildingId" IS DISTINCT FROM NEW."BuildingId"
                   OR policy."EvidenceSha256" IS DISTINCT FROM NEW."ProposalSha256"
                   OR policy."ProposedByUserId" = NEW."ReviewedByUserId"
                   OR NEW."ReviewedAtUtc" < policy."ProposedAtUtc" THEN
                    RAISE EXCEPTION 'OI-07 review lacks independent matching proposal'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $review$;
            CREATE TRIGGER "TR_BM_RoomChangePolicyReview_Guard"
              BEFORE INSERT OR UPDATE OR DELETE
              ON "BuildingManagementRoomChangeBusinessPolicyReviews"
              FOR EACH ROW EXECUTE FUNCTION "BM_GuardRoomChangePolicyReview"();

            CREATE FUNCTION "BM_NoTruncateRoomChangePolicyReviews"()
            RETURNS trigger LANGUAGE plpgsql AS $hold$
            BEGIN
                RAISE EXCEPTION 'OI-07 review history cannot be truncated'
                    USING ERRCODE = '23514';
            END;
            $hold$;
            CREATE TRIGGER "TR_BM_RoomChangePolicyReview_NoTruncate"
              BEFORE TRUNCATE ON "BuildingManagementRoomChangeBusinessPolicyReviews"
              FOR EACH STATEMENT EXECUTE FUNCTION "BM_NoTruncateRoomChangePolicyReviews"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementRoomChangeBusinessPolicyReviews"
              IN ACCESS EXCLUSIVE MODE;
            DO $down$
            BEGIN
                IF EXISTS (SELECT 1 FROM
                    "BuildingManagementRoomChangeBusinessPolicyReviews" LIMIT 1) THEN
                    RAISE EXCEPTION 'Refuse rollback with root-reviewed OI-07 evidence'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $down$;
            DROP TRIGGER "TR_BM_RoomChangePolicyReview_NoTruncate"
              ON "BuildingManagementRoomChangeBusinessPolicyReviews";
            DROP FUNCTION "BM_NoTruncateRoomChangePolicyReviews"();
            DROP TRIGGER "TR_BM_RoomChangePolicyReview_Guard"
              ON "BuildingManagementRoomChangeBusinessPolicyReviews";
            DROP FUNCTION "BM_GuardRoomChangePolicyReview"();
            DROP TABLE "BuildingManagementRoomChangeBusinessPolicyReviews";
            """);
    }
}
