using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations
{
    /// <inheritdoc />
    public partial class Phase2G2B1_AddMeterReplacements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BuildingManagementMeterReplacements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OldMeterId = table.Column<Guid>(type: "uuid", nullable: false),
                    NewMeterId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoomId = table.Column<Guid>(type: "uuid", nullable: false),
                    UtilityType = table.Column<int>(type: "integer", nullable: false),
                    ReplacementDate = table.Column<DateTime>(type: "date", nullable: false),
                    OldPreviousReading = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    OldFinalReading = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    NewInitialReading = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ExtraProperties = table.Column<string>(type: "text", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastModificationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastModifierId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuildingManagementMeterReplacements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BM_MeterReplacements_NewMeter",
                        column: x => x.NewMeterId,
                        principalTable: "BuildingManagementUtilityMeters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BM_MeterReplacements_OldMeter",
                        column: x => x.OldMeterId,
                        principalTable: "BuildingManagementUtilityMeters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BM_MeterReplacements_Room",
                        column: x => x.RoomId,
                        principalTable: "BuildingManagementRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementMeterReplacements_NewMeterId",
                table: "BuildingManagementMeterReplacements",
                column: "NewMeterId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementMeterReplacements_OldMeterId",
                table: "BuildingManagementMeterReplacements",
                column: "OldMeterId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementMeterReplacements_RoomId_ReplacementDate",
                table: "BuildingManagementMeterReplacements",
                columns: new[] { "RoomId", "ReplacementDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BuildingManagementMeterReplacements");
        }
    }
}
