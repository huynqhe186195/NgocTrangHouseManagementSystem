using BuildingManagement.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Mockup 3's component palette has eleven object kinds; the first migration of
/// P4.5 shipped only seven of them, so the designer could not place the laundry
/// room, storage, balcony or tree the mockup draws. Widening the enum is enough
/// for the API, but the table also carries a CHECK that pins Kind to 1..7, so
/// that constraint has to move with it.
/// Hand-written SQL for the same reason as every migration after 20261021.
/// </summary>
[DbContext(typeof(BuildingManagementDbContext))]
[Migration("20261115170000_P45WidenFloorLayoutItemKind")]
public sealed class P45WidenFloorLayoutItemKind : Migration
{
    protected override void Up(MigrationBuilder b) => b.Sql("""
        ALTER TABLE "BuildingManagementFloorLayoutItems"
          DROP CONSTRAINT "CK_BM_FloorLayoutItem_Kind";
        ALTER TABLE "BuildingManagementFloorLayoutItems"
          ADD CONSTRAINT "CK_BM_FloorLayoutItem_Kind" CHECK ("Kind" BETWEEN 1 AND 11);
        """);

    protected override void Down(MigrationBuilder b) => b.Sql("""
        ALTER TABLE "BuildingManagementFloorLayoutItems"
          DROP CONSTRAINT "CK_BM_FloorLayoutItem_Kind";
        ALTER TABLE "BuildingManagementFloorLayoutItems"
          ADD CONSTRAINT "CK_BM_FloorLayoutItem_Kind" CHECK ("Kind" BETWEEN 1 AND 7);
        """);
}
