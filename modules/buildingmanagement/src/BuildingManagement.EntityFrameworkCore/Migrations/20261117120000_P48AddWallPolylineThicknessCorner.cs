using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// A wall is only stored as a box today, so it can never bend. The mockup's
/// storey outline is a polyline, so a wall gains a centreline (Points, the same
/// JSON shape Room.LayoutPoints already uses), its own thickness, and a corner
/// radius that turns the bevel at each bend into an arc. All three are nullable:
/// a wall saved before this migration keeps drawing from its box.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261117120000_P48AddWallPolylineThicknessCorner")]
public sealed class P48AddWallPolylineThicknessCorner : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        ALTER TABLE "BuildingManagementFloorLayoutItems"
          ADD COLUMN "Points" varchar(4000) NULL,
          ADD COLUMN "Thickness" numeric(10,2) NULL,
          ADD COLUMN "CornerRadius" numeric(10,2) NULL;
        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        ALTER TABLE "BuildingManagementFloorLayoutItems"
          DROP COLUMN "CornerRadius",
          DROP COLUMN "Thickness",
          DROP COLUMN "Points";
        """);
}
