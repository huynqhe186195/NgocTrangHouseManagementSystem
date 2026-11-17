using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// The mockup furnishes every room with a bed, a wardrobe, a desk and the like.
/// Those are layout objects the owner places, so they join the palette as kinds
/// 13..21. The table's CHECK still pins Kind to 1..12, so it widens here too.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261117110000_P47AddFurnitureLayoutItemKinds")]
public sealed class P47AddFurnitureLayoutItemKinds : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        ALTER TABLE "BuildingManagementFloorLayoutItems"
          DROP CONSTRAINT "CK_BM_FloorLayoutItem_Kind";
        ALTER TABLE "BuildingManagementFloorLayoutItems"
          ADD CONSTRAINT "CK_BM_FloorLayoutItem_Kind" CHECK ("Kind" BETWEEN 1 AND 21);
        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        DELETE FROM "BuildingManagementFloorLayoutItems" WHERE "Kind" BETWEEN 13 AND 21;
        ALTER TABLE "BuildingManagementFloorLayoutItems"
          DROP CONSTRAINT "CK_BM_FloorLayoutItem_Kind";
        ALTER TABLE "BuildingManagementFloorLayoutItems"
          ADD CONSTRAINT "CK_BM_FloorLayoutItem_Kind" CHECK ("Kind" BETWEEN 1 AND 12);
        """);
}
