using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations
{
    /// <inheritdoc />
    public partial class Add_ContractRenewalHold_Entity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BuildingManagementContractRenewalHolds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoomId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CompletedContractId = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("PK_BuildingManagementContractRenewalHolds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BuildingManagementContractRenewalHolds_BuildingManagementCo~",
                        column: x => x.CompletedContractId,
                        principalTable: "BuildingManagementContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BuildingManagementContractRenewalHolds_BuildingManagementC~1",
                        column: x => x.CurrentContractId,
                        principalTable: "BuildingManagementContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BuildingManagementContractRenewalHolds_BuildingManagementRo~",
                        column: x => x.RoomId,
                        principalTable: "BuildingManagementRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementContractRenewalHolds_CompletedContractId",
                table: "BuildingManagementContractRenewalHolds",
                column: "CompletedContractId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementContractRenewalHolds_CurrentContractId",
                table: "BuildingManagementContractRenewalHolds",
                column: "CurrentContractId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementContractRenewalHolds_RoomId_Status_Expire~",
                table: "BuildingManagementContractRenewalHolds",
                columns: new[] { "RoomId", "Status", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BuildingManagementContractRenewalHolds");
        }
    }
}
