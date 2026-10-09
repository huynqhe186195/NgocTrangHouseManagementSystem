using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations
{
    /// <inheritdoc />
    public partial class Add_UtilityMeter_Entity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BuildingManagementUtilityMeters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RoomId = table.Column<Guid>(type: "uuid", nullable: false),
                    UtilityType = table.Column<int>(type: "integer", nullable: false),
                    MeterCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InitialReading = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    InstalledDate = table.Column<DateTime>(type: "date", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ExtraProperties = table.Column<string>(type: "text", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastModificationTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastModifierId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    DeleterId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeletionTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuildingManagementUtilityMeters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BuildingManagementUtilityMeters_BuildingManagementRooms_Roo~",
                        column: x => x.RoomId,
                        principalTable: "BuildingManagementRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementUtilityMeters_MeterCode",
                table: "BuildingManagementUtilityMeters",
                column: "MeterCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementUtilityMeters_RoomId_UtilityType",
                table: "BuildingManagementUtilityMeters",
                columns: new[] { "RoomId", "UtilityType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BuildingManagementUtilityMeters");
        }
    }
}
