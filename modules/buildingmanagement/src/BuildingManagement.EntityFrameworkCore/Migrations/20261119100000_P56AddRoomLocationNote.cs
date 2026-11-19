using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// The mockups give every room a "Vị trí" line ("Gần cầu thang", "View nội
/// khu") that sat empty because the schema had nowhere to keep it. It is free
/// text for the same reason RoomType is: it describes the room to a tenant in
/// the owner's words, it is not a coordinate the system computes. Nullable and
/// add-only, so every room seeded before this migration keeps working.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261119100000_P56AddRoomLocationNote")]
public sealed class P56AddRoomLocationNote : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        ALTER TABLE "BuildingManagementRooms"
          ADD COLUMN "LocationNote" varchar(128) NULL;
        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        ALTER TABLE "BuildingManagementRooms"
          DROP COLUMN "LocationNote";
        """);
}
