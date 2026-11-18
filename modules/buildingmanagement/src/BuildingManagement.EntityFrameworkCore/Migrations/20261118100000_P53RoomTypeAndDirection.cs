using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// The room screen shows "Hướng" and "Loại phòng", but a room only stored a
/// description. Both are nullable so every room created before this migration
/// keeps working: a null simply renders as "Chưa cập nhật".
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261118100000_P53RoomTypeAndDirection")]
public sealed class P53RoomTypeAndDirection : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        ALTER TABLE "BuildingManagementRooms"
          ADD COLUMN "RoomType" varchar(64) NULL,
          ADD COLUMN "Direction" integer NULL;
        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        ALTER TABLE "BuildingManagementRooms"
          DROP COLUMN "Direction",
          DROP COLUMN "RoomType";
        """);
}
