using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Source-only closeout provenance: tenant parties and ABP extensible
/// business fields are historical contract evidence after signed closeout.
/// Existing ABP audit fields (timestamps, modifiers, concurrency stamps)
/// may still change without replacing that evidence.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261029216000_SliceBFreezeClosedContractPartiesAndExtras")]
public sealed class SliceBFreezeClosedContractPartiesAndExtras : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
            CREATE FUNCTION "BM_GuardSignedContractTenantHistory"()
            RETURNS trigger LANGUAGE plpgsql AS $contract_party_guard$
            DECLARE frozen boolean;
            BEGIN
                IF TG_OP = 'TRUNCATE' THEN
                    IF EXISTS (
                        SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals" LIMIT 1
                    ) THEN
                        RAISE EXCEPTION 'Signed contract membership history cannot be truncated'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NULL;
                END IF;

                -- Serialize direct SQL edits with the signed-approval INSERT,
                -- which locks the same Contract row FOR UPDATE. For a
                -- reassignment, lock BOTH contract IDs in stable order.
                IF TG_OP = 'INSERT' THEN
                    PERFORM 1 FROM "BuildingManagementContracts"
                     WHERE "Id" = NEW."ContractId" FOR UPDATE;
                ELSIF TG_OP = 'DELETE' THEN
                    PERFORM 1 FROM "BuildingManagementContracts"
                     WHERE "Id" = OLD."ContractId" FOR UPDATE;
                ELSE
                    PERFORM 1 FROM "BuildingManagementContracts"
                     WHERE "Id" IN (OLD."ContractId", NEW."ContractId")
                     ORDER BY "Id" FOR UPDATE;
                END IF;

                SELECT EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals"
                     WHERE "ContractId" = CASE WHEN TG_OP = 'INSERT'
                                              THEN NEW."ContractId"
                                              ELSE OLD."ContractId" END
                        OR (TG_OP = 'UPDATE' AND "ContractId" = NEW."ContractId")
                ) INTO frozen;

                IF NOT frozen THEN
                    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
                END IF;
                IF TG_OP <> 'UPDATE'
                   OR NEW."Id" IS DISTINCT FROM OLD."Id"
                   OR NEW."ContractId" IS DISTINCT FROM OLD."ContractId"
                   OR NEW."TenantId" IS DISTINCT FROM OLD."TenantId"
                   OR NEW."Role" IS DISTINCT FROM OLD."Role"
                   OR NEW."IsDeleted" IS DISTINCT FROM OLD."IsDeleted"
                THEN
                    RAISE EXCEPTION 'Signed contract tenants cannot be added, removed or rewritten'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $contract_party_guard$;
            CREATE TRIGGER "TR_BM_SignedContractTenant_Freeze"
                BEFORE INSERT OR UPDATE OR DELETE ON "BuildingManagementContractTenants"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardSignedContractTenantHistory"();
            CREATE TRIGGER "TR_BM_SignedContractTenant_NoTruncate"
                BEFORE TRUNCATE ON "BuildingManagementContractTenants"
                FOR EACH STATEMENT EXECUTE FUNCTION "BM_GuardSignedContractTenantHistory"();

            CREATE FUNCTION "BM_GuardSignedCloseoutExtraProperties"()
            RETURNS trigger LANGUAGE plpgsql AS $closed_extras$
            DECLARE is_closed boolean;
            DECLARE request_id uuid;
            BEGIN
                IF NEW."ExtraProperties" IS NOT DISTINCT FROM OLD."ExtraProperties" THEN
                    RETURN NEW;
                END IF;
                IF TG_TABLE_NAME = 'BuildingManagementContracts' THEN
                    SELECT EXISTS (
                        SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals"
                         WHERE "ContractId" = OLD."Id"
                    ) INTO is_closed;
                ELSIF TG_TABLE_NAME = 'BuildingManagementMoveOutRequests' THEN
                    SELECT EXISTS (
                        SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals"
                         WHERE "MoveOutRequestId" = OLD."Id"
                    ) INTO is_closed;
                ELSIF TG_TABLE_NAME = 'BuildingManagementMoveOutChecklistTasks' THEN
                    request_id := OLD."MoveOutRequestId";
                    -- The preceding checklist freeze trigger takes this
                    -- same request row lock, serializing approval races.
                    SELECT EXISTS (
                        SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals"
                         WHERE "MoveOutRequestId" = request_id
                    ) INTO is_closed;
                ELSE
                    RAISE EXCEPTION 'Unknown signed history table'
                        USING ERRCODE = '23514';
                END IF;
                IF is_closed THEN
                    RAISE EXCEPTION 'Signed closeout extended business metadata is immutable'
                        USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END;
            $closed_extras$;
            CREATE TRIGGER "TR_BM_SignedContract_Extras"
                BEFORE UPDATE ON "BuildingManagementContracts"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardSignedCloseoutExtraProperties"();
            CREATE TRIGGER "TR_BM_SignedMoveOut_Extras"
                BEFORE UPDATE ON "BuildingManagementMoveOutRequests"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardSignedCloseoutExtraProperties"();
            CREATE TRIGGER "TR_BM_SignedChecklist_Extras"
                BEFORE UPDATE ON "BuildingManagementMoveOutChecklistTasks"
                FOR EACH ROW EXECUTE FUNCTION "BM_GuardSignedCloseoutExtraProperties"();
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            LOCK TABLE "BuildingManagementContractDepositCloseoutApprovals",
                       "BuildingManagementContracts",
                       "BuildingManagementContractTenants",
                       "BuildingManagementMoveOutRequests",
                       "BuildingManagementMoveOutChecklistTasks"
                IN ACCESS EXCLUSIVE MODE;
            DO $contract_parties_down$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "BuildingManagementContractDepositCloseoutApprovals" LIMIT 1
                ) THEN
                    RAISE EXCEPTION 'Signed contract parties exist; refuse historical-evidence downgrade'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $contract_parties_down$;
            DROP TRIGGER "TR_BM_SignedContractTenant_Freeze"
                ON "BuildingManagementContractTenants";
            DROP TRIGGER "TR_BM_SignedContractTenant_NoTruncate"
                ON "BuildingManagementContractTenants";
            DROP TRIGGER "TR_BM_SignedContract_Extras"
                ON "BuildingManagementContracts";
            DROP TRIGGER "TR_BM_SignedMoveOut_Extras"
                ON "BuildingManagementMoveOutRequests";
            DROP TRIGGER "TR_BM_SignedChecklist_Extras"
                ON "BuildingManagementMoveOutChecklistTasks";
            DROP FUNCTION "BM_GuardSignedCloseoutExtraProperties"();
            DROP FUNCTION "BM_GuardSignedContractTenantHistory"();
            """);
    }
}
