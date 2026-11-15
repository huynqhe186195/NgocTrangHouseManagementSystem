using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// The building-management mockup draws a status badge on every floor row and
/// a cover photo on the building card, but neither had anywhere to live: Floor
/// carried only building/number/name/description, and only rooms could store
/// images. This migration adds both — Floors gets an integer Status backed by
/// a CHECK, and the cover image gets its own metadata + content pair exactly
/// like the room photo tables (bytes in a separate row so listing buildings
/// never drags megabytes through a query).
/// Hand-written SQL for the same reason as every migration after 20261021.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261116100000_P46FloorStatusAndBuildingPhoto")]
public sealed class P46FloorStatusAndBuildingPhoto : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        ALTER TABLE "BuildingManagementFloors"
          ADD COLUMN "Status" integer NOT NULL DEFAULT 1;
        ALTER TABLE "BuildingManagementFloors"
          ADD CONSTRAINT "CK_BM_Floor_Status" CHECK ("Status" IN (1, 2, 3));

        CREATE TABLE "BuildingManagementBuildingPhotos"(
          "Id" uuid PRIMARY KEY,
          "BuildingId" uuid NOT NULL REFERENCES
            "Buildings"("Id") ON DELETE RESTRICT,
          "FileName" varchar(256) NOT NULL,
          "ContentType" varchar(64) NOT NULL,
          "SizeBytes" bigint NOT NULL,
          "ExtraProperties" text NOT NULL,
          "ConcurrencyStamp" varchar(40) NOT NULL,
          "CreationTime" timestamptz NOT NULL,
          "CreatorId" uuid NULL,
          "LastModificationTime" timestamptz NULL,
          "LastModifierId" uuid NULL,
          "IsDeleted" boolean NOT NULL DEFAULT FALSE,
          "DeleterId" uuid NULL,
          "DeletionTime" timestamptz NULL,
          CONSTRAINT "CK_BM_BuildingPhoto_Size" CHECK ("SizeBytes" > 0)
        );
        -- One cover image per building: upload replaces, never accumulates.
        CREATE UNIQUE INDEX "IX_BuildingManagementBuildingPhotos_BuildingId"
          ON "BuildingManagementBuildingPhotos"("BuildingId")
          WHERE "IsDeleted" = FALSE;

        CREATE TABLE "BuildingManagementBuildingPhotoContents"(
          "PhotoId" uuid PRIMARY KEY REFERENCES
            "BuildingManagementBuildingPhotos"("Id") ON DELETE CASCADE,
          "Content" bytea NOT NULL,
          CONSTRAINT "CK_BM_BuildingPhotoContent_Size" CHECK (
            octet_length("Content") BETWEEN 1 AND 5242880
          )
        );
        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        DROP TABLE "BuildingManagementBuildingPhotoContents";
        DROP TABLE "BuildingManagementBuildingPhotos";

        ALTER TABLE "BuildingManagementFloors"
          DROP CONSTRAINT "CK_BM_Floor_Status";
        ALTER TABLE "BuildingManagementFloors"
          DROP COLUMN "Status";
        """);
}
