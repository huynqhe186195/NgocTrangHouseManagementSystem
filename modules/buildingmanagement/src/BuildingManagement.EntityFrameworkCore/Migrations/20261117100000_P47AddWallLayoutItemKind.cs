using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// The mockup draws the storey's outer wall as a component the owner places and
/// stretches, so it joins the palette as kind 12. Widening the enum is not
/// enough on its own: the table's CHECK pins Kind to 1..11.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261117100000_P47AddWallLayoutItemKind")]
public sealed class P47AddWallLayoutItemKind : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        ALTER TABLE "BuildingManagementFloorLayoutItems"
          DROP CONSTRAINT "CK_BM_FloorLayoutItem_Kind";
        ALTER TABLE "BuildingManagementFloorLayoutItems"
          ADD CONSTRAINT "CK_BM_FloorLayoutItem_Kind" CHECK ("Kind" BETWEEN 1 AND 12);
        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        DELETE FROM "BuildingManagementFloorLayoutItems" WHERE "Kind" = 12;
        ALTER TABLE "BuildingManagementFloorLayoutItems"
          DROP CONSTRAINT "CK_BM_FloorLayoutItem_Kind";
        ALTER TABLE "BuildingManagementFloorLayoutItems"
          ADD CONSTRAINT "CK_BM_FloorLayoutItem_Kind" CHECK ("Kind" BETWEEN 1 AND 11);
        """);
}
