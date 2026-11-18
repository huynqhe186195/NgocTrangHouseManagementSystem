using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// The room screen quotes electricity, water and a service fee, and lists the
/// house rules beside them. UtilityRate cannot carry these: billing reads it
/// as invoice evidence, so quoting a price there would quietly change what a
/// tenant is charged. This is a separate one-row-per-building display record.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261118110000_P53BuildingPublicInfo")]
public sealed class P53BuildingPublicInfo : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        CREATE TABLE "BuildingManagementBuildingPublicInfos"(
          "Id" uuid PRIMARY KEY,
          "BuildingId" uuid NOT NULL REFERENCES
            "Buildings"("Id") ON DELETE RESTRICT,
          "ElectricityUnitPrice" numeric(18,0) NULL,
          "WaterUnitPrice" numeric(18,0) NULL,
          "ServiceFee" numeric(18,0) NULL,
          "OtherInfo" varchar(1000) NULL,
          "HouseRulesJson" varchar(4000) NULL,
          "ExtraProperties" text NOT NULL,
          "ConcurrencyStamp" varchar(40) NOT NULL,
          "CreationTime" timestamptz NOT NULL,
          "CreatorId" uuid NULL,
          "LastModificationTime" timestamptz NULL,
          "LastModifierId" uuid NULL,
          "IsDeleted" boolean NOT NULL DEFAULT FALSE,
          "DeleterId" uuid NULL,
          "DeletionTime" timestamptz NULL
        );
        -- One published-info row per building.
        CREATE UNIQUE INDEX "IX_BuildingManagementBuildingPublicInfos_BuildingId"
          ON "BuildingManagementBuildingPublicInfos"("BuildingId")
          WHERE "IsDeleted" = FALSE;
        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        DROP TABLE "BuildingManagementBuildingPublicInfos";
        """);
}
