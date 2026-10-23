using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations
{
    /// <inheritdoc />
    public partial class Phase2G2A_AddUtilityIntegrityConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Exclusion enforces non-overlapping date windows across ALL historical
            // UtilityRate rows, matching the existing soft-delete business rule.
            // btree_gist supports UUID/int equality in a GiST exclusion constraint.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");
            migrationBuilder.Sql(@"
                ALTER TABLE ""BuildingManagementUtilityRates""
                ADD CONSTRAINT ""EX_BuildingManagementUtilityRates_NoOverlap""
                EXCLUDE USING gist (
                    ""BuildingId"" WITH =,
                    ""UtilityType"" WITH =,
                    daterange(""EffectiveFrom"", ""EffectiveTo"", '[]') WITH &&
                );
            ");
            migrationBuilder.DropIndex(
                name: "IX_BuildingManagementUtilityMeters_RoomId_UtilityType",
                table: "BuildingManagementUtilityMeters");

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementUtilityMeters_RoomId_UtilityType",
                table: "BuildingManagementUtilityMeters",
                columns: new[] { "RoomId", "UtilityType" },
                unique: true,
                filter: "\"IsActive\" = TRUE AND \"IsDeleted\" = FALSE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""BuildingManagementUtilityRates""
                DROP CONSTRAINT IF EXISTS ""EX_BuildingManagementUtilityRates_NoOverlap"";
            ");
            // Do not drop btree_gist: another schema object may depend on it.
            migrationBuilder.DropIndex(
                name: "IX_BuildingManagementUtilityMeters_RoomId_UtilityType",
                table: "BuildingManagementUtilityMeters");

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementUtilityMeters_RoomId_UtilityType",
                table: "BuildingManagementUtilityMeters",
                columns: new[] { "RoomId", "UtilityType" });
        }
    }
}
