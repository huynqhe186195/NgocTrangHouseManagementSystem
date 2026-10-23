using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations
{
    /// <inheritdoc />
    public partial class Phase2G3A4_AddInvoiceDraftSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BuildingManagementInvoices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoomId = table.Column<Guid>(type: "uuid", nullable: false),
                    BillingYear = table.Column<int>(type: "integer", nullable: false),
                    BillingMonth = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ProvisionalUtilityTotal = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    ExtraProperties = table.Column<string>(type: "text", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastModificationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastModifierId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuildingManagementInvoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BuildingManagementInvoices_BuildingManagementContracts_Cont~",
                        column: x => x.ContractId,
                        principalTable: "BuildingManagementContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BuildingManagementInvoices_BuildingManagementRooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "BuildingManagementRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BuildingManagementInvoiceItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UtilityType = table.Column<int>(type: "integer", nullable: false),
                    UtilityRateId = table.Column<Guid>(type: "uuid", nullable: true),
                    RateStatus = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RecordedQuantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: true),
                    ProvisionalAmount = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    CoverageIssuesJson = table.Column<string>(type: "text", nullable: false),
                    CreationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastModificationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastModifierId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuildingManagementInvoiceItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BuildingManagementInvoiceItems_BuildingManagementInvoices_I~",
                        column: x => x.InvoiceId,
                        principalTable: "BuildingManagementInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BuildingManagementInvoiceItems_BuildingManagementUtilityRat~",
                        column: x => x.UtilityRateId,
                        principalTable: "BuildingManagementUtilityRates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BuildingManagementInvoiceItemMeterSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UtilityMeterId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceType = table.Column<int>(type: "integer", nullable: false),
                    RecordedDate = table.Column<DateTime>(type: "date", nullable: false),
                    PreviousReading = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    CurrentReading = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuildingManagementInvoiceItemMeterSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BuildingManagementInvoiceItemMeterSources_BuildingManagemen~",
                        column: x => x.InvoiceItemId,
                        principalTable: "BuildingManagementInvoiceItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BuildingManagementInvoiceItemMeterSources_BuildingManageme~1",
                        column: x => x.UtilityMeterId,
                        principalTable: "BuildingManagementUtilityMeters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementInvoiceItemMeterSources_UtilityMeterId",
                table: "BuildingManagementInvoiceItemMeterSources",
                column: "UtilityMeterId");

            migrationBuilder.CreateIndex(
                name: "UX_BM_InvoiceSource_Global",
                table: "BuildingManagementInvoiceItemMeterSources",
                columns: new[] { "SourceType", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BM_InvoiceSource_Item_Type_Source",
                table: "BuildingManagementInvoiceItemMeterSources",
                columns: new[] { "InvoiceItemId", "SourceType", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementInvoiceItems_UtilityRateId",
                table: "BuildingManagementInvoiceItems",
                column: "UtilityRateId");

            migrationBuilder.CreateIndex(
                name: "UX_BM_InvoiceItem_Invoice_Utility",
                table: "BuildingManagementInvoiceItems",
                columns: new[] { "InvoiceId", "UtilityType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BM_Invoice_Contract_Period",
                table: "BuildingManagementInvoices",
                columns: new[] { "ContractId", "BillingYear", "BillingMonth" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BM_Invoice_Room_Period",
                table: "BuildingManagementInvoices",
                columns: new[] { "RoomId", "BillingYear", "BillingMonth" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BuildingManagementInvoiceItemMeterSources");

            migrationBuilder.DropTable(
                name: "BuildingManagementInvoiceItems");

            migrationBuilder.DropTable(
                name: "BuildingManagementInvoices");
        }
    }
}
