using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Additive D03 source-binding ledger. One immutable original room-service
/// line per monthly candidate/service; checked against BOTH frozen monthly
/// evidence and the original tariff version before the INSERT commits.
/// This migration does not enable monetary posting, or alter old invoices.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261104150000_Phase2HRoomServiceInvoiceSourceBindings")]
public sealed class Phase2HRoomServiceInvoiceSourceBindings : Migration
{
    protected override void Up(MigrationBuilder builder) => builder.Sql("""
        CREATE TABLE "BuildingManagementRoomServiceInvoiceSourceBindings" (
          "MonthlyCandidateId" uuid NOT NULL,
          "ContractId" uuid NOT NULL,
          "RoomId" uuid NOT NULL,
          "BuildingId" uuid NOT NULL,
          "BillingYear" integer NOT NULL,
          "BillingMonth" integer NOT NULL,
          "ServiceId" uuid NOT NULL,
          "ServiceCode" varchar(64) NOT NULL,
          "OriginalTariffVersionId" uuid NOT NULL,
          "OriginalTariffVersion" integer NOT NULL,
          "OriginalTariffSha256" varchar(64) NOT NULL,
          "OriginalFullPeriodUnitPriceVnd" numeric(18,0) NOT NULL,
          "BillingUnit" integer NOT NULL,
          "ChargingRule" integer NOT NULL,
          "ServiceFromVietnam" date NOT NULL,
          "ServiceThroughVietnam" date NOT NULL,
          "VerifiedOccupantCount" integer NOT NULL,
          "VerifiedMeterQuantity" numeric NULL,
          "VerifiedChargeSourceReference" varchar(500) NOT NULL,
          "FrozenAuxiliaryLineAmountVnd" numeric(18,0) NOT NULL,
          "OriginalMonthlyCandidateSha256" varchar(64) NOT NULL,
          "EvidenceSha256" varchar(64) NOT NULL,
          CONSTRAINT "PK_BM_RoomServiceInvoiceBinding"
            PRIMARY KEY ("MonthlyCandidateId","ServiceId"),
          CONSTRAINT "FK_BM_RoomServiceBinding_Candidate"
            FOREIGN KEY ("MonthlyCandidateId")
            REFERENCES "BuildingManagementMonthlyInvoiceCandidates"("Id")
            ON DELETE RESTRICT,
          CONSTRAINT "FK_BM_RoomServiceBinding_Tariff"
            FOREIGN KEY ("OriginalTariffVersionId")
            REFERENCES "BuildingManagementRoomScopedServiceTariffVersions"("Id")
            ON DELETE RESTRICT,
          CONSTRAINT "CK_BM_RoomServiceInvoiceBinding_Valid" CHECK (
            "ContractId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "RoomId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "BuildingId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "ServiceId"<>'00000000-0000-0000-0000-000000000000'::uuid
            AND "BillingYear" BETWEEN 2000 AND 2200
            AND "BillingMonth" BETWEEN 1 AND 12
            AND length(btrim("ServiceCode")) BETWEEN 1 AND 64
            AND "OriginalTariffVersion">0
            AND "OriginalFullPeriodUnitPriceVnd">=0
            AND "BillingUnit" IN (1,2,3)
            AND "ChargingRule" IN (1,2,3)
            AND (("BillingUnit"=3)=("ChargingRule"=3))
            AND "ServiceThroughVietnam">="ServiceFromVietnam"
            AND "VerifiedOccupantCount">=0
            AND "VerifiedMeterQuantity">=0
            AND length(btrim("VerifiedChargeSourceReference")) BETWEEN 12 AND 500
            AND "FrozenAuxiliaryLineAmountVnd">0
            AND "OriginalTariffSha256"~'^[a-f0-9]{64}$'
            AND "OriginalMonthlyCandidateSha256"~'^[a-f0-9]{64}$'
            AND "EvidenceSha256"~'^[a-f0-9]{64}$'
          )
        );

        -- The same frozen line cannot be rebound under another service ID,
        -- case variant, or a different current room tariff.
        CREATE UNIQUE INDEX "UX_BM_RoomServiceInvoiceBinding_Code"
         ON "BuildingManagementRoomServiceInvoiceSourceBindings"
            ("MonthlyCandidateId",upper("ServiceCode"));
        CREATE INDEX "IX_BM_RoomServiceInvoice_SourceCode"
         ON "BuildingManagementRoomServiceInvoiceSourceBindings"
            ("MonthlyCandidateId","ServiceCode");
        CREATE INDEX "IX_BM_RoomServiceInvoice_TariffVersion"
         ON "BuildingManagementRoomServiceInvoiceSourceBindings"
            ("OriginalTariffVersionId");

        CREATE FUNCTION "BM_2HVerifyRoomServiceInvoiceBinding"()
        RETURNS trigger LANGUAGE plpgsql AS $binding$
        DECLARE
         source record;
         tariff record;
         line_count integer;
         line_amount numeric;
         line_reference text;
         multiplier numeric;
         expected_amount numeric;
         days_in_month integer;
         active_days integer;
        BEGIN
         SELECT * INTO source
          FROM "BuildingManagementMonthlyInvoiceCandidates"
         WHERE "Id"=NEW."MonthlyCandidateId" FOR SHARE;
         SELECT * INTO tariff
          FROM "BuildingManagementRoomScopedServiceTariffVersions"
         WHERE "Id"=NEW."OriginalTariffVersionId" FOR SHARE;

         IF source."Id" IS NULL OR tariff."Id" IS NULL
           OR NEW."ContractId" IS DISTINCT FROM source."ContractId"
           OR NEW."RoomId" IS DISTINCT FROM source."RoomId"
           OR NEW."BuildingId" IS DISTINCT FROM source."BuildingId"
           OR NEW."BillingYear" IS DISTINCT FROM source."BillingYear"
           OR NEW."BillingMonth" IS DISTINCT FROM source."BillingMonth"
           OR NEW."OriginalMonthlyCandidateSha256" IS DISTINCT FROM
              source."FrozenEvidenceSha256"
           OR NEW."RoomId" IS DISTINCT FROM tariff."RoomId"
           OR NEW."ServiceId" IS DISTINCT FROM tariff."ServiceId"
           OR NEW."OriginalTariffVersion" IS DISTINCT FROM tariff."Version"
           OR NEW."OriginalTariffSha256" IS DISTINCT FROM tariff."EvidenceSha256"
           OR NEW."OriginalFullPeriodUnitPriceVnd" IS DISTINCT FROM
              tariff."UnitPriceVnd"
           OR NEW."BillingUnit" IS DISTINCT FROM tariff."BillingUnit"
           OR NEW."ChargingRule" IS DISTINCT FROM tariff."ChargingRule"
           OR NEW."ServiceFromVietnam" < tariff."EffectiveFrom"
           OR (tariff."EffectiveThrough" IS NOT NULL AND
              NEW."ServiceThroughVietnam">tariff."EffectiveThrough")
           OR NEW."BillingYear"<>extract(year from NEW."ServiceFromVietnam")::integer
           OR NEW."BillingMonth"<>extract(month from NEW."ServiceFromVietnam")::integer
           OR NEW."BillingYear"<>extract(year from NEW."ServiceThroughVietnam")::integer
           OR NEW."BillingMonth"<>extract(month from NEW."ServiceThroughVietnam")::integer
           OR tariff."ApprovedAtUtc">source."ReviewedAtUtc"
           OR EXISTS(
               SELECT 1 FROM "BuildingManagementMonthlyIssuedInvoiceLedger"
               WHERE "CandidateId"=NEW."MonthlyCandidateId")
           OR EXISTS(
               SELECT 1 FROM "BuildingManagementRoomScopedServiceTariffVersions" newer
               WHERE newer."RoomId"=NEW."RoomId"
                 AND newer."ServiceId"=NEW."ServiceId"
                 AND newer."Version">NEW."OriginalTariffVersion"
                 AND newer."EffectiveFrom"<=NEW."ServiceThroughVietnam"
           ) THEN
           RAISE EXCEPTION
             'Source binding must match original same-room reviewed candidate and currently effective historical tariff'
             USING ERRCODE='23514';
         END IF;

         IF (source."FrozenEvidenceJson"::jsonb->>'AuxiliaryFeeDecision')::integer
            IS DISTINCT FROM 2 THEN
           RAISE EXCEPTION 'Original candidate does not authorize itemized fees'
             USING ERRCODE='23514';
         END IF;

         SELECT count(*)::integer,
                max((fee->>'Amount')::numeric),
                max(fee->>'ContractOrRateReference')
           INTO line_count,line_amount,line_reference
           FROM jsonb_array_elements(
             coalesce(source."FrozenEvidenceJson"::jsonb->'AuxiliaryCharges',
                      '[]'::jsonb)) fee
          WHERE upper(btrim(fee->>'ServiceCode'))=upper(btrim(NEW."ServiceCode"));

         IF line_count IS DISTINCT FROM 1
            OR line_reference IS DISTINCT FROM tariff."SignedPolicyReference"
            OR line_amount IS DISTINCT FROM NEW."FrozenAuxiliaryLineAmountVnd" THEN
           RAISE EXCEPTION 'Signed frozen auxiliary line reference/amount mismatch'
             USING ERRCODE='23514';
         END IF;

         IF NEW."BillingUnit"=1 THEN
           IF NEW."VerifiedOccupantCount"<1 OR
              NEW."VerifiedMeterQuantity" IS NOT NULL THEN
             RAISE EXCEPTION 'Per-person rate needs verified occupants, no meter'
               USING ERRCODE='23514';
           END IF;
           multiplier:=NEW."VerifiedOccupantCount";
         ELSIF NEW."BillingUnit"=2 THEN
           IF NEW."VerifiedMeterQuantity" IS NOT NULL THEN
             RAISE EXCEPTION 'Per-room fee cannot use a meter quantity'
               USING ERRCODE='23514';
           END IF;
           multiplier:=1;
         ELSE
           IF NEW."VerifiedMeterQuantity" IS NULL OR
              NEW."VerifiedMeterQuantity"<0 THEN
             RAISE EXCEPTION 'Metered service requires verified nonnegative use'
               USING ERRCODE='23514';
           END IF;
           multiplier:=NEW."VerifiedMeterQuantity";
         END IF;

         days_in_month:=extract(day from
           (date_trunc('month',NEW."ServiceFromVietnam"::timestamp)
             + interval '1 month - 1 day'))::integer;
         active_days:=NEW."ServiceThroughVietnam"-NEW."ServiceFromVietnam"+1;
         expected_amount:=round(tariff."UnitPriceVnd"*multiplier*
             CASE WHEN NEW."ChargingRule"=2
                 THEN active_days::numeric/days_in_month::numeric
                 ELSE 1::numeric END,0);

         IF expected_amount IS DISTINCT FROM NEW."FrozenAuxiliaryLineAmountVnd"
           OR expected_amount<=0 THEN
           RAISE EXCEPTION 'Frozen service charge does not equal original room rate arithmetic'
             USING ERRCODE='23514';
         END IF;
         RETURN NEW;
        END;
        $binding$;

        CREATE TRIGGER "TR_BM_2H_RoomServiceInvoiceBinding_Source"
          BEFORE INSERT ON "BuildingManagementRoomServiceInvoiceSourceBindings"
          FOR EACH ROW EXECUTE FUNCTION "BM_2HVerifyRoomServiceInvoiceBinding"();

        CREATE FUNCTION "BM_2HDenyRoomServiceInvoiceBindingMutation"()
        RETURNS trigger LANGUAGE plpgsql AS $immutable$
        BEGIN
          RAISE EXCEPTION 'Frozen room-service invoice source evidence is append-only'
            USING ERRCODE='23514';
        END;
        $immutable$;

        CREATE TRIGGER "TR_BM_2H_RoomServiceInvoiceBinding_Immutable"
          BEFORE UPDATE OR DELETE
          ON "BuildingManagementRoomServiceInvoiceSourceBindings"
          FOR EACH ROW EXECUTE FUNCTION "BM_2HDenyRoomServiceInvoiceBindingMutation"();
        CREATE TRIGGER "TR_BM_2H_RoomServiceInvoiceBinding_NoTruncate"
          BEFORE TRUNCATE
          ON "BuildingManagementRoomServiceInvoiceSourceBindings"
          FOR EACH STATEMENT
          EXECUTE FUNCTION "BM_2HDenyRoomServiceInvoiceBindingMutation"();

        -- Explicit read-only readiness for the later, authenticated monthly
        -- issuer. This migration DOES NOT wire a money/issuance writer.
        CREATE FUNCTION "BM_2HRoomServiceInvoiceBindingsReady"(p_candidate uuid)
        RETURNS boolean LANGUAGE plpgsql STABLE AS $ready$
        DECLARE
          source record;
          required_count integer;
          bound_count integer;
          bound_total numeric;
          review_kind integer;
        BEGIN
          SELECT * INTO source
            FROM "BuildingManagementMonthlyInvoiceCandidates"
           WHERE "Id"=p_candidate;
          IF source."Id" IS NULL THEN RETURN FALSE; END IF;
          review_kind:=(source."FrozenEvidenceJson"::jsonb->>'AuxiliaryFeeDecision')::integer;
          required_count:=jsonb_array_length(coalesce(
            source."FrozenEvidenceJson"::jsonb->'AuxiliaryCharges','[]'::jsonb));
          SELECT count(*)::integer,coalesce(sum("FrozenAuxiliaryLineAmountVnd"),0)
            INTO bound_count,bound_total
            FROM "BuildingManagementRoomServiceInvoiceSourceBindings"
           WHERE "MonthlyCandidateId"=p_candidate;
          IF review_kind=1 THEN
             RETURN required_count=0 AND bound_count=0 AND source."AuxiliaryAmount"=0;
          END IF;
          -- A later Owner edit can supersede the tariff AFTER the original
          -- binding was frozen. Reject stale/overlapping source versions at
          -- issuance readiness too, not only at the binding INSERT.
          IF EXISTS (
             SELECT 1
               FROM "BuildingManagementRoomServiceInvoiceSourceBindings" binding
               JOIN "BuildingManagementRoomScopedServiceTariffVersions" tariff
                 ON tariff."Id"=binding."OriginalTariffVersionId"
              WHERE binding."MonthlyCandidateId"=p_candidate
                AND EXISTS (
                 SELECT 1 FROM "BuildingManagementRoomScopedServiceTariffVersions" newer
                  WHERE newer."RoomId"=tariff."RoomId"
                    AND newer."ServiceId"=tariff."ServiceId"
                    AND newer."Version">tariff."Version"
                    AND newer."EffectiveFrom"<=binding."ServiceThroughVietnam"
                )
          ) THEN RETURN FALSE; END IF;
          RETURN review_kind=2 AND required_count>0
             AND required_count=bound_count
             AND bound_total=source."AuxiliaryAmount";
        END;
        $ready$;
        """);

    protected override void Down(MigrationBuilder builder) => builder.Sql("""
        LOCK TABLE "BuildingManagementRoomServiceInvoiceSourceBindings"
          IN ACCESS EXCLUSIVE MODE;
        DO $retained$
        BEGIN
          IF EXISTS(
            SELECT 1 FROM "BuildingManagementRoomServiceInvoiceSourceBindings"
              LIMIT 1) THEN
            RAISE EXCEPTION 'Cannot drop original room service invoice source evidence'
              USING ERRCODE='23514';
          END IF;
        END;
        $retained$;
        DROP TABLE "BuildingManagementRoomServiceInvoiceSourceBindings";
        DROP FUNCTION "BM_2HVerifyRoomServiceInvoiceBinding"();
        DROP FUNCTION "BM_2HDenyRoomServiceInvoiceBindingMutation"();
        DROP FUNCTION "BM_2HRoomServiceInvoiceBindingsReady"(uuid);
        """);
}
