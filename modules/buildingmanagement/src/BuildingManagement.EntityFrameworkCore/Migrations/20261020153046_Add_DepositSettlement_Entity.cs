using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations
{
    /// <inheritdoc />
    public partial class Add_DepositSettlement_Entity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BuildingManagementDepositSettlements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false),
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceRoomReservationId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceContractId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetContractId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ExtraProperties = table.Column<string>(type: "text", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastModificationTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastModifierId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuildingManagementDepositSettlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BuildingManagementDepositSettlements_BuildingManagementCont~",
                        column: x => x.SourceContractId,
                        principalTable: "BuildingManagementContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BuildingManagementDepositSettlements_BuildingManagementCon~1",
                        column: x => x.TargetContractId,
                        principalTable: "BuildingManagementContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BuildingManagementDepositSettlements_BuildingManagementPaym~",
                        column: x => x.PaymentId,
                        principalTable: "BuildingManagementPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BuildingManagementDepositSettlements_BuildingManagementRoom~",
                        column: x => x.SourceRoomReservationId,
                        principalTable: "BuildingManagementRoomReservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementDepositSettlements_PaymentId",
                table: "BuildingManagementDepositSettlements",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementDepositSettlements_SourceContractId",
                table: "BuildingManagementDepositSettlements",
                column: "SourceContractId");

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementDepositSettlements_SourceRoomReservationId",
                table: "BuildingManagementDepositSettlements",
                column: "SourceRoomReservationId");

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementDepositSettlements_TargetContractId",
                table: "BuildingManagementDepositSettlements",
                column: "TargetContractId");

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementDepositSettlements_Type_Status",
                table: "BuildingManagementDepositSettlements",
                columns: new[] { "Type", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BuildingManagementDepositSettlements");
        }
    }
}
