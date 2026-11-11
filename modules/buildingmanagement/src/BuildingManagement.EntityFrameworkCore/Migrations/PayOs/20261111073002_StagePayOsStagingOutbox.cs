using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations.PayOs
{
    /// <inheritdoc />
    public partial class StagePayOsStagingOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BuildingManagementPayOsStagingOutbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentIntentId = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildingId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderCode = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastObservedProviderStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LastAttemptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExtraProperties = table.Column<string>(type: "text", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuildingManagementPayOsStagingOutbox", x => x.Id);
                    table.CheckConstraint("CK_PayOsOutbox_CompletedOnlyWhenCompleted", "(\"Status\" = 1 AND \"CompletedAtUtc\" IS NOT NULL) OR (\"Status\" <> 1 AND \"CompletedAtUtc\" IS NULL)");
                    table.CheckConstraint("CK_PayOsOutbox_OrderCodePositive", "\"OrderCode\" > 0");
                    table.ForeignKey(
                        name: "FK_BuildingManagementPayOsStagingOutbox_BuildingManagementPayO~",
                        columns: x => new { x.PaymentIntentId, x.BuildingId, x.OrderCode },
                        principalTable: "BuildingManagementPayOsPaymentIntents",
                        principalColumns: new[] { "Id", "BuildingId", "OrderCode" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayOsStagingOutbox_BuildingId_Status",
                table: "BuildingManagementPayOsStagingOutbox",
                columns: new[] { "BuildingId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayOsStagingOutbox_PaymentIntentId_Buildi~",
                table: "BuildingManagementPayOsStagingOutbox",
                columns: new[] { "PaymentIntentId", "BuildingId", "OrderCode" });

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayOsStagingOutbox_PaymentIntentId_Kind",
                table: "BuildingManagementPayOsStagingOutbox",
                columns: new[] { "PaymentIntentId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayOsStagingOutbox_Status_NextAttemptAtUtc",
                table: "BuildingManagementPayOsStagingOutbox",
                columns: new[] { "Status", "NextAttemptAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BuildingManagementPayOsStagingOutbox");
        }
    }
}
