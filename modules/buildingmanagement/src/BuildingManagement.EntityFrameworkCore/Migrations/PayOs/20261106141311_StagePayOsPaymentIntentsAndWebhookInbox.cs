using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations.PayOs
{
    /// <inheritdoc />
    public partial class StagePayOsPaymentIntentsAndWebhookInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BuildingManagementPayOsPaymentIntents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildingId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    OrderCode = table.Column<long>(type: "bigint", nullable: false),
                    AmountVnd = table.Column<long>(type: "bigint", nullable: false),
                    PaymentLinkId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ProviderReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ExtraProperties = table.Column<string>(type: "text", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuildingManagementPayOsPaymentIntents", x => x.Id);
                    table.UniqueConstraint("AK_BuildingManagementPayOsPaymentIntents_Id_BuildingId_OrderCo~", x => new { x.Id, x.BuildingId, x.OrderCode });
                    table.CheckConstraint("CK_PayOsIntent_AmountPositive", "\"AmountVnd\" > 0");
                    table.CheckConstraint("CK_PayOsIntent_ExactlyOneTarget", "(\"InvoiceId\" IS NULL) <> (\"PaymentId\" IS NULL)");
                    table.CheckConstraint("CK_PayOsIntent_OrderPositive", "\"OrderCode\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "BuildingManagementPayOsWebhookInbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildingId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentIntentId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderCode = table.Column<long>(type: "bigint", nullable: false),
                    ProviderReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PayloadSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ExtraProperties = table.Column<string>(type: "text", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuildingManagementPayOsWebhookInbox", x => x.Id);
                    table.CheckConstraint("CK_PayOsInbox_Digest64", "length(\"PayloadSha256\") = 64");
                    table.CheckConstraint("CK_PayOsInbox_OrderCodePositive", "\"OrderCode\" > 0");
                    table.ForeignKey(
                        name: "FK_BuildingManagementPayOsWebhookInbox_BuildingManagementPayOs~",
                        columns: x => new { x.PaymentIntentId, x.BuildingId, x.OrderCode },
                        principalTable: "BuildingManagementPayOsPaymentIntents",
                        principalColumns: new[] { "Id", "BuildingId", "OrderCode" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayOsPaymentIntents_BuildingId_InvoiceId",
                table: "BuildingManagementPayOsPaymentIntents",
                columns: new[] { "BuildingId", "InvoiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayOsPaymentIntents_BuildingId_Status",
                table: "BuildingManagementPayOsPaymentIntents",
                columns: new[] { "BuildingId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayOsPaymentIntents_OrderCode",
                table: "BuildingManagementPayOsPaymentIntents",
                column: "OrderCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayOsPaymentIntents_PaymentId",
                table: "BuildingManagementPayOsPaymentIntents",
                column: "PaymentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayOsPaymentIntents_PaymentLinkId",
                table: "BuildingManagementPayOsPaymentIntents",
                column: "PaymentLinkId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayOsPaymentIntents_ProviderReference",
                table: "BuildingManagementPayOsPaymentIntents",
                column: "ProviderReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayOsWebhookInbox_BuildingId_Status_Recei~",
                table: "BuildingManagementPayOsWebhookInbox",
                columns: new[] { "BuildingId", "Status", "ReceivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayOsWebhookInbox_OrderCode",
                table: "BuildingManagementPayOsWebhookInbox",
                column: "OrderCode");

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayOsWebhookInbox_PaymentIntentId_Buildin~",
                table: "BuildingManagementPayOsWebhookInbox",
                columns: new[] { "PaymentIntentId", "BuildingId", "OrderCode" });

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayOsWebhookInbox_PaymentIntentId_Provide~",
                table: "BuildingManagementPayOsWebhookInbox",
                columns: new[] { "PaymentIntentId", "ProviderReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementPayOsWebhookInbox_ProviderReference",
                table: "BuildingManagementPayOsWebhookInbox",
                column: "ProviderReference",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BuildingManagementPayOsWebhookInbox");

            migrationBuilder.DropTable(
                name: "BuildingManagementPayOsPaymentIntents");
        }
    }
}
