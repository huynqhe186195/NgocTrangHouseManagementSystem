using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// One financial source deposit may have at most one immutable H4
/// signed disposition proposal until a separately audited supersession
/// protocol is designed. This prevents cross-request double claims by
/// bypassing the HTTP service. No cash or contract mutation occurs.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261104180000_Phase2HDepositDispositionSourceExclusivity")]
public sealed class Phase2HDepositDispositionSourceExclusivity : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        LOCK TABLE "BuildingManagementRoomChangeDepositDispositionIntents"
          IN ACCESS EXCLUSIVE MODE;
        DO $exclusivity$
        BEGIN
          IF EXISTS(
            SELECT 1
              FROM "BuildingManagementRoomChangeDepositDispositionIntents"
             GROUP BY "SourceContractId"
            HAVING count(*) > 1
          ) THEN
            RAISE EXCEPTION
              'H4 source deposit is already claimed by multiple original signed intents'
              USING ERRCODE='23514';
          END IF;
        END;
        $exclusivity$;

        CREATE UNIQUE INDEX "UX_BM_H4Intent_SourceContract"
          ON "BuildingManagementRoomChangeDepositDispositionIntents"
             ("SourceContractId");
        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        LOCK TABLE "BuildingManagementRoomChangeDepositDispositionIntents"
          IN ACCESS EXCLUSIVE MODE;
        DO $retain$
        BEGIN
          IF EXISTS(
            SELECT 1
              FROM "BuildingManagementRoomChangeDepositDispositionIntents"
             LIMIT 1
          ) THEN
            RAISE EXCEPTION
              'Cannot remove H4 source exclusivity with signed intent history'
              USING ERRCODE='23514';
          END IF;
        END;
        $retain$;
        DROP INDEX "UX_BM_H4Intent_SourceContract";
        """);
}
