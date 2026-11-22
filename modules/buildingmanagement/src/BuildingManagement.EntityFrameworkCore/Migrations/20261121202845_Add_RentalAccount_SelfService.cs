using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations;

/// <summary>
/// Adds the RentalAccount scope and Identity-user links used by the tenant
/// self-service read endpoint. IdentityUserId is intentionally application-level
/// because Identity is stored in a separate database.
/// </summary>
public partial class Add_RentalAccount_SelfService : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "BuildingManagementRentalAccounts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AccountNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                ExtraProperties = table.Column<string>(type: "text", nullable: false),
                ConcurrencyStamp = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                CreationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatorId = table.Column<Guid>(type: "uuid", nullable: true),
                LastModificationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastModifierId = table.Column<Guid>(type: "uuid", nullable: true),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                DeleterId = table.Column<Guid>(type: "uuid", nullable: true),
                DeletionTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BuildingManagementRentalAccounts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "BuildingManagementRentalAccountUsers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RentalAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                IdentityUserId = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                AccessRole = table.Column<int>(type: "integer", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatorId = table.Column<Guid>(type: "uuid", nullable: true),
                LastModificationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastModifierId = table.Column<Guid>(type: "uuid", nullable: true),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                DeleterId = table.Column<Guid>(type: "uuid", nullable: true),
                DeletionTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BuildingManagementRentalAccountUsers", x => x.Id);
                table.ForeignKey(
                    name: "FK_BuildingManagementRentalAccountUsers_BuildingManagementRentalAccounts_RentalAccountId",
                    column: x => x.RentalAccountId,
                    principalTable: "BuildingManagementRentalAccounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.AddColumn<Guid>(
            name: "RentalAccountId",
            table: "BuildingManagementContracts",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_BuildingManagementRentalAccounts_AccountNumber",
            table: "BuildingManagementRentalAccounts",
            column: "AccountNumber",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_BuildingManagementRentalAccounts_IsActive",
            table: "BuildingManagementRentalAccounts",
            column: "IsActive");

        migrationBuilder.CreateIndex(
            name: "IX_BuildingManagementRentalAccountUsers_IdentityUserId",
            table: "BuildingManagementRentalAccountUsers",
            column: "IdentityUserId");

        migrationBuilder.CreateIndex(
            name: "IX_BuildingManagementRentalAccountUsers_RentalAccountId_IdentityUserId",
            table: "BuildingManagementRentalAccountUsers",
            columns: new[] { "RentalAccountId", "IdentityUserId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_BuildingManagementRentalAccountUsers_RentalAccountId_IsActive",
            table: "BuildingManagementRentalAccountUsers",
            columns: new[] { "RentalAccountId", "IsActive" });

        migrationBuilder.CreateIndex(
            name: "IX_BuildingManagementRentalAccountUsers_TenantId",
            table: "BuildingManagementRentalAccountUsers",
            column: "TenantId");

        migrationBuilder.CreateIndex(
            name: "IX_BuildingManagementContracts_RentalAccountId",
            table: "BuildingManagementContracts",
            column: "RentalAccountId");

        migrationBuilder.AddForeignKey(
            name: "FK_BuildingManagementContracts_BuildingManagementRentalAccounts_RentalAccountId",
            table: "BuildingManagementContracts",
            column: "RentalAccountId",
            principalTable: "BuildingManagementRentalAccounts",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_BuildingManagementContracts_BuildingManagementRentalAccounts_RentalAccountId",
            table: "BuildingManagementContracts");

        migrationBuilder.DropIndex(
            name: "IX_BuildingManagementContracts_RentalAccountId",
            table: "BuildingManagementContracts");

        migrationBuilder.DropColumn(
            name: "RentalAccountId",
            table: "BuildingManagementContracts");

        migrationBuilder.DropTable(
            name: "BuildingManagementRentalAccountUsers");

        migrationBuilder.DropTable(
            name: "BuildingManagementRentalAccounts");
    }
}
