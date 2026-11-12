using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Source-only, transaction-deferred contract and move-out closeout fence.
/// Immutable approval INSERT occurs before the application marks both sources
/// terminal. At COMMIT both must be completed, not merely audit-flushed.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029214000_SliceBValidateCompletedCloseoutAtCommit")]
public sealed class SliceBValidateCompletedCloseoutAtCommit : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE FUNCTION "BM_ValidateDepositCloseoutCommittedEnd"()
            RETURNS trigger LANGUAGE plpgsql AS $committed_end$
            DECLARE contract_status integer;
            DECLARE ended_at timestamptz;
            DECLARE request_status integer;
            DECLARE completed_at timestamptz;
            DECLARE request_contract uuid;
            BEGIN
                -- This constraint trigger is initially DEFERRED. PostgreSQL
                -- checks the final same-transaction Contract.End and
                -- MoveOutRequest.Complete values at commit, not the brief
                -- intermediate Active/Approved audit-only state.
                SELECT c."Status", c."ActualEndedAt", r."Status",
                       r."CompletedAt", r."ContractId"
                  INTO contract_status, ended_at, request_status,
                       completed_at, request_contract
                  FROM "BuildingManagementContracts" c
                  JOIN "BuildingManagementMoveOutRequests" r
                    ON r."Id" = NEW."MoveOutRequestId"
                 WHERE c."Id" = NEW."ContractId";

                IF NOT FOUND OR request_contract IS DISTINCT FROM NEW."ContractId"
                   OR contract_status IS DISTINCT FROM 3
                   OR request_status IS DISTINCT FROM 4
                   OR ended_at IS NULL OR completed_at IS NULL
                   OR ended_at < NEW."ApprovedAtUtc"
                   OR completed_at < NEW."ApprovedAtUtc"
                THEN
                    RAISE EXCEPTION 'Signed deposit closeout must commit with ended contract and completed move-out'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NULL;
            END;
            $committed_end$;

            CREATE CONSTRAINT TRIGGER "TR_BM_DepositCloseout_RequireCompletedAtCommit"
                AFTER INSERT ON "BuildingManagementContractDepositCloseoutApprovals"
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION "BM_ValidateDepositCloseoutCommittedEnd"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementContractDepositCloseoutApprovals"
                IN ACCESS EXCLUSIVE MODE;
            DO $end_down$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals" LIMIT 1
                ) THEN
                    RAISE EXCEPTION 'Completed closeout exists; refuse deferred-integrity downgrade'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $end_down$;
            DROP TRIGGER "TR_BM_DepositCloseout_RequireCompletedAtCommit"
                ON "BuildingManagementContractDepositCloseoutApprovals";
            DROP FUNCTION "BM_ValidateDepositCloseoutCommittedEnd"();
            """);
    }
}
