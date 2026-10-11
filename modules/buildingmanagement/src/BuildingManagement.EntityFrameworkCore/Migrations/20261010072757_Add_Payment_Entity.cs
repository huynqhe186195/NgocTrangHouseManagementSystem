using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations
{
    /// <inheritdoc />
    public partial class Add_Payment_Entity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BuildingManagementPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<int>(type: "integer", nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    Channel = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false),
                    PaidAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RoomReservationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: true),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    DepositDisposition = table.Column<int>(type: "integer", nullable: true),
                    TransactionReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
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
                    table.PrimaryKey("PK_BuildingManagementPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BuildingManagementPayments_BuildingManagementContracts_Cont~",
                        column: x => x.ContractId,
                        principalTable: "BuildingManagementContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BuildingManagementPayments_BuildingManagementRoomReservatio~",
                        column: x => x.RoomReservationId,
                        principalTable: "BuildingManagementRoomReservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BuildingManagementPayments_BuildingManagementTenants_Tenant~",
                        column: x => x.TenantId,
                        principalTable: "BuildingManagementTenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayments_ContractId",
                table: "BuildingManagementPayments",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayments_InvoiceId",
                table: "BuildingManagementPayments",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayments_PaymentNumber",
                table: "BuildingManagementPayments",
                column: "PaymentNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayments_Purpose_Status_PaidAt",
                table: "BuildingManagementPayments",
                columns: new[] { "Purpose", "Status", "PaidAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayments_RoomReservationId",
                table: "BuildingManagementPayments",
                column: "RoomReservationId");

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayments_TenantId",
                table: "BuildingManagementPayments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayments_TransactionReference",
                table: "BuildingManagementPayments",
                column: "TransactionReference",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BuildingManagementPayments");
        }
    }
}
