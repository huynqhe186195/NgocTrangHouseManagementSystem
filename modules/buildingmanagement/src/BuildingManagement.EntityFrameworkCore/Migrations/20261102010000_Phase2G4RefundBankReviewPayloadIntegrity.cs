using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Additive fail-closed validation of the independent manual refund proof.
/// The earlier attestation table permitted structurally valid but unrelated
/// JSON and arbitrary 64-character hashes on direct SQL INSERT. PostgreSQL
/// must verify the archived payload even if application validation is bypassed.
/// SHA-256 is an integrity checksum, NOT a bank API or a digital signature.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261102010000_Phase2G4RefundBankReviewPayloadIntegrity")]
public sealed class Phase2G4RefundBankReviewPayloadIntegrity : Migration
{
    protected override void Up(MigrationBuilder b)
    {
        b.Sql("""
            ALTER TABLE "BuildingManagementContractDepositRefundBankReviews"
              ADD CONSTRAINT "CK_BM_RefundReview_ArchivedPayload"
              CHECK (
                "ReviewSha256" =
                  encode(sha256(convert_to("ReviewJson",'UTF8')),'hex')
                AND ("ReviewJson"::jsonb ->> 'ContractId')
                    IS NOT DISTINCT FROM "ContractId"::text
                AND ("ReviewJson"::jsonb ->> 'MoveOutRequestId')
                    IS NOT DISTINCT FROM "MoveOutRequestId"::text
                AND ("ReviewJson"::jsonb ->> 'DepositDecisionId')
                    IS NOT DISTINCT FROM "DepositDecisionId"::text
                AND ("ReviewJson"::jsonb ->> 'BuildingId')
                    IS NOT DISTINCT FROM "BuildingId"::text
                AND ("ReviewJson"::jsonb ->> 'ReviewedByUserId')
                    IS NOT DISTINCT FROM "ReviewedByUserId"::text
                AND ("ReviewJson"::jsonb ->> 'ReviewedAtUtc')::timestamptz
                    IS NOT DISTINCT FROM "ReviewedAtUtc"
                AND ("ReviewJson"::jsonb ->> 'PaidAtUtc')::timestamptz
                    IS NOT DISTINCT FROM "PaidAtUtc"
                AND ("ReviewJson"::jsonb ->> 'VerifiedRefundAmountVnd')::numeric
                    IS NOT DISTINCT FROM "VerifiedRefundAmountVnd"
                AND ("ReviewJson"::jsonb ->> 'BankTransactionReference')
                    IS NOT DISTINCT FROM "BankTransactionReference"
                AND ("ReviewJson"::jsonb ->> 'BankStatementEvidenceReference')
                    IS NOT DISTINCT FROM "BankStatementEvidenceReference"
                AND ("ReviewJson"::jsonb ->> 'SignedReviewReference')
                    IS NOT DISTINCT FROM "SignedReviewReference"
                AND "BankStatementEvidenceReference" <> "SignedReviewReference"
              );
            """);
    }

    protected override void Down(MigrationBuilder b)
    {
        b.Sql("""
            LOCK TABLE "BuildingManagementContractDepositRefundBankReviews"
              IN ACCESS EXCLUSIVE MODE;
            DO $down$
            BEGIN
              IF EXISTS (SELECT 1
                  FROM "BuildingManagementContractDepositRefundBankReviews" LIMIT 1)
              THEN
                RAISE EXCEPTION
                    'Cannot weaken signed refund payload validation with recorded history'
                    USING ERRCODE='23514';
              END IF;
            END; $down$;
            ALTER TABLE "BuildingManagementContractDepositRefundBankReviews"
              DROP CONSTRAINT "CK_BM_RefundReview_ArchivedPayload";
            """);
    }
}
