using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// P4.5 mockup data layer. Adds the fields the five Figma screens need but the
/// Room/Floor model never carried: monthly rent, real floor-plan geometry on
/// both rooms and non-room objects, in-room equipment and room photos.
/// Photos store metadata only; the bytes live in the ABP blob tables.
/// Hand-written SQL because every migration after 20261021 is hand-written and
/// the checked-in model snapshot is intentionally not maintained.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261115120000_P45RoomRentEquipmentPhotosLayout")]
public sealed class P45RoomRentEquipmentPhotosLayout : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        ALTER TABLE "BuildingManagementRooms"
          ADD COLUMN "MonthlyRent" numeric(18,0) NULL,
          ADD COLUMN "LayoutX" numeric(10,2) NULL,
          ADD COLUMN "LayoutY" numeric(10,2) NULL,
          ADD COLUMN "LayoutWidth" numeric(10,2) NULL,
          ADD COLUMN "LayoutHeight" numeric(10,2) NULL,
          ADD COLUMN "LayoutShape" integer NULL,
          ADD COLUMN "LayoutRotationDegrees" numeric(6,2) NULL,
          ADD COLUMN "LayoutPoints" varchar(4000) NULL;

        CREATE TABLE "BuildingManagementRoomEquipment"(
          "Id" uuid PRIMARY KEY,
          "RoomId" uuid NOT NULL REFERENCES
            "BuildingManagementRooms"("Id") ON DELETE RESTRICT,
          "Name" varchar(128) NOT NULL,
          "Category" varchar(64) NULL,
          "Quantity" integer NOT NULL,
          "Condition" integer NOT NULL,
          "InstalledDate" date NULL,
          "Notes" varchar(500) NULL,
          "ExtraProperties" text NOT NULL,
          "ConcurrencyStamp" varchar(40) NOT NULL,
          "CreationTime" timestamptz NOT NULL,
          "CreatorId" uuid NULL,
          "LastModificationTime" timestamptz NULL,
          "LastModifierId" uuid NULL,
          "IsDeleted" boolean NOT NULL DEFAULT FALSE,
          "DeleterId" uuid NULL,
          "DeletionTime" timestamptz NULL,
          CONSTRAINT "CK_BM_RoomEquipment_Quantity" CHECK ("Quantity" >= 0),
          CONSTRAINT "CK_BM_RoomEquipment_Condition" CHECK ("Condition" IN (1, 2, 3))
        );
        CREATE INDEX "IX_BuildingManagementRoomEquipment_RoomId"
          ON "BuildingManagementRoomEquipment"("RoomId");

        CREATE TABLE "BuildingManagementRoomPhotos"(
          "Id" uuid PRIMARY KEY,
          "RoomId" uuid NOT NULL REFERENCES
            "BuildingManagementRooms"("Id") ON DELETE RESTRICT,
          "FileName" varchar(256) NOT NULL,
          "ContentType" varchar(64) NOT NULL,
          "SizeBytes" bigint NOT NULL,
          "DisplayOrder" integer NOT NULL,
          "BlobName" varchar(256) NOT NULL,
          "ExtraProperties" text NOT NULL,
          "ConcurrencyStamp" varchar(40) NOT NULL,
          "CreationTime" timestamptz NOT NULL,
          "CreatorId" uuid NULL,
          "LastModificationTime" timestamptz NULL,
          "LastModifierId" uuid NULL,
          "IsDeleted" boolean NOT NULL DEFAULT FALSE,
          "DeleterId" uuid NULL,
          "DeletionTime" timestamptz NULL,
          CONSTRAINT "CK_BM_RoomPhoto_Size" CHECK ("SizeBytes" > 0),
          CONSTRAINT "CK_BM_RoomPhoto_Type" CHECK (
            "ContentType" IN ('image/png', 'image/jpeg', 'image/webp')
          )
        );
        CREATE INDEX "IX_BuildingManagementRoomPhotos_RoomId_DisplayOrder"
          ON "BuildingManagementRoomPhotos"("RoomId", "DisplayOrder");
        CREATE UNIQUE INDEX "UX_BM_RoomPhoto_Blob"
          ON "BuildingManagementRoomPhotos"("BlobName");

        CREATE TABLE "BuildingManagementRoomPhotoContents"(
          "PhotoId" uuid PRIMARY KEY REFERENCES
            "BuildingManagementRoomPhotos"("Id") ON DELETE CASCADE,
          "Content" bytea NOT NULL,
          CONSTRAINT "CK_BM_RoomPhotoContent_Size" CHECK (
            octet_length("Content") BETWEEN 1 AND 5242880
          )
        );

        CREATE TABLE "BuildingManagementFloorLayoutItems"(
          "Id" uuid PRIMARY KEY,
          "FloorId" uuid NOT NULL REFERENCES
            "BuildingManagementFloors"("Id") ON DELETE RESTRICT,
          "Kind" integer NOT NULL,
          "Label" varchar(128) NULL,
          "X" numeric(10,2) NOT NULL,
          "Y" numeric(10,2) NOT NULL,
          "Width" numeric(10,2) NOT NULL,
          "Height" numeric(10,2) NOT NULL,
          "ExtraProperties" text NOT NULL,
          "ConcurrencyStamp" varchar(40) NOT NULL,
          "CreationTime" timestamptz NOT NULL,
          "CreatorId" uuid NULL,
          "LastModificationTime" timestamptz NULL,
          "LastModifierId" uuid NULL,
          "IsDeleted" boolean NOT NULL DEFAULT FALSE,
          "DeleterId" uuid NULL,
          "DeletionTime" timestamptz NULL,
          CONSTRAINT "CK_BM_FloorLayoutItem_Kind" CHECK ("Kind" BETWEEN 1 AND 7),
          CONSTRAINT "CK_BM_FloorLayoutItem_Box" CHECK (
            "Width" >= 0 AND "Height" >= 0
          )
        );
        CREATE INDEX "IX_BuildingManagementFloorLayoutItems_FloorId"
          ON "BuildingManagementFloorLayoutItems"("FloorId");
        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        DROP TABLE "BuildingManagementFloorLayoutItems";
        DROP TABLE "BuildingManagementRoomPhotoContents";
        DROP TABLE "BuildingManagementRoomPhotos";
        DROP TABLE "BuildingManagementRoomEquipment";

        ALTER TABLE "BuildingManagementRooms"
          DROP COLUMN "LayoutPoints",
          DROP COLUMN "LayoutRotationDegrees",
          DROP COLUMN "LayoutShape",
          DROP COLUMN "LayoutHeight",
          DROP COLUMN "LayoutWidth",
          DROP COLUMN "LayoutY",
          DROP COLUMN "LayoutX",
          DROP COLUMN "MonthlyRent";
        """);
}
