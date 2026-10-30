using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Source-only fix for the financial closeout serialization key.
/// The original COALESCE(source,target) misses the receiving contract
/// of transfers. Lock every OLD and NEW contract in stable UUID order.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029218000_SliceBLockBothDepositSettlementContracts")]
public sealed class SliceBLockBothDepositSettlementContracts : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE OR REPLACE FUNCTION "BM_GuardFinalizedDepositSettlements"()
            RETURNS trigger LANGUAGE plpgsql AS $settlement_guard$
            BEGIN
                -- Approval INSERT holds its Contract row FOR UPDATE.
                -- Source and target must BOTH participate in that same
                -- serialisation lock, regardless of settlement Type.
                -- Stable ordering also avoids A -> B / B -> A lock cycles.
                IF TG_OP = 'INSERT' THEN
                    PERFORM c."Id" FROM "BuildingManagementContracts" c
                     WHERE c."Id" IN (
                         SELECT NEW."SourceContractId"
                         UNION SELECT NEW."TargetContractId"
                     )
                     ORDER BY c."Id" FOR UPDATE;
                ELSIF TG_OP = 'DELETE' THEN
                    PERFORM c."Id" FROM "BuildingManagementContracts" c
                     WHERE c."Id" IN (
                         SELECT OLD."SourceContractId"
                         UNION SELECT OLD."TargetContractId"
                     )
                     ORDER BY c."Id" FOR UPDATE;
                ELSE
                    -- For reassignment protect the previous AND new scope.
                    PERFORM c."Id" FROM "BuildingManagementContracts" c
                     WHERE c."Id" IN (
                         SELECT OLD."SourceContractId"
                         UNION SELECT OLD."TargetContractId"
                         UNION SELECT NEW."SourceContractId"
                         UNION SELECT NEW."TargetContractId"
                     )
                     ORDER BY c."Id" FOR UPDATE;
                END IF;

                IF (TG_OP IN ('UPDATE','DELETE') AND EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals"
                     WHERE "ContractId" = OLD."SourceContractId"
                        OR "ContractId" = OLD."TargetContractId"
                        OR "RefundSettlementId" = OLD."Id"
                        OR "ForfeitSettlementId" = OLD."Id"
                )) OR
                   (TG_OP IN ('INSERT','UPDATE') AND EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals"
                     WHERE "ContractId" = NEW."SourceContractId"
                        OR "ContractId" = NEW."TargetContractId"
                )) THEN
                    RAISE EXCEPTION 'Approved contract deposit settlement history cannot be modified'
                        USING ERRCODE = '23514';
                END IF;
                RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
            END;
            $settlement_guard$;
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementContractDepositCloseoutApprovals",
                       "BuildingManagementDepositSettlements",
                       "BuildingManagementContracts"
                IN ACCESS EXCLUSIVE MODE;
            DO $settlement_lock_down$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals" LIMIT 1
                ) THEN
                    RAISE EXCEPTION 'Signed deposit closeout exists; refuse settlement-lock downgrade'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $settlement_lock_down$;

            -- Restore the exact source-only 20261029204000 implementation
            -- only when no irreversible signed closeout history exists.
            CREATE OR REPLACE FUNCTION "BM_GuardFinalizedDepositSettlements"()
            RETURNS trigger LANGUAGE plpgsql AS $settlement_guard$
            DECLARE affected_contract uuid;
            BEGIN
                affected_contract := CASE WHEN TG_OP = 'DELETE'
                    THEN COALESCE(OLD."SourceContractId", OLD."TargetContractId")
                    ELSE COALESCE(NEW."SourceContractId", NEW."TargetContractId")
                END;
                IF affected_contract IS NOT NULL THEN
                    PERFORM 1 FROM "BuildingManagementContracts"
                    WHERE "Id" = affected_contract FOR UPDATE;
                END IF;
                IF (TG_OP IN ('UPDATE','DELETE') AND EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals"
                     WHERE "ContractId" = OLD."SourceContractId"
                        OR "ContractId" = OLD."TargetContractId"
                        OR "RefundSettlementId" = OLD."Id"
                        OR "ForfeitSettlementId" = OLD."Id"
                )) OR
                   (TG_OP IN ('INSERT','UPDATE') AND EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals"
                     WHERE "ContractId" = NEW."SourceContractId"
                        OR "ContractId" = NEW."TargetContractId"
                )) THEN
                    RAISE EXCEPTION 'Approved contract deposit settlement history cannot be modified'
                        USING ERRCODE = '23514';
                END IF;
                RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
            END;
            $settlement_guard$;
            """);
    }
}
