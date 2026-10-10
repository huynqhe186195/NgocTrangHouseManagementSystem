using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations
{
    /// <inheritdoc />
    public partial class Add_RoomReservation_Entity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BuildingManagementRoomReservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservationNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RoomId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpectedMoveInDate = table.Column<DateTime>(type: "date", nullable: false),
                    QuotedMonthlyRent = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false),
                    RequiredDepositAmount = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ReservedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DepositDueDate = table.Column<DateTime>(type: "date", nullable: true),
                    ConvertedContractId = table.Column<Guid>(type: "uuid", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CancellationNotes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CancellationReason = table.Column<int>(type: "integer", nullable: true),
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
                    table.PrimaryKey("PK_BuildingManagementRoomReservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BuildingManagementRoomReservations_BuildingManagementContra~",
                        column: x => x.ConvertedContractId,
                        principalTable: "BuildingManagementContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BuildingManagementRoomReservations_BuildingManagementRooms_~",
                        column: x => x.RoomId,
                        principalTable: "BuildingManagementRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BuildingManagementRoomReservations_BuildingManagementTenant~",
                        column: x => x.TenantId,
                        principalTable: "BuildingManagementTenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementRoomReservations_ConvertedContractId",
                table: "BuildingManagementRoomReservations",
                column: "ConvertedContractId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementRoomReservations_ReservationNumber",
                table: "BuildingManagementRoomReservations",
                column: "ReservationNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementRoomReservations_RoomId_Status_ExpectedMo~",
                table: "BuildingManagementRoomReservations",
                columns: new[] { "RoomId", "Status", "ExpectedMoveInDate" });

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementRoomReservations_TenantId",
                table: "BuildingManagementRoomReservations",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BuildingManagementRoomReservations");
        }
    }
}
