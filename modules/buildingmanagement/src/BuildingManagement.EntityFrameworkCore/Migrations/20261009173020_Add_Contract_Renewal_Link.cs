using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingManagement.Migrations
{
    /// <inheritdoc />
    public partial class Add_Contract_Renewal_Link : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RenewedFromContractId",
                table: "BuildingManagementContracts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildingManagementContracts_RenewedFromContractId",
                table: "BuildingManagementContracts",
                column: "RenewedFromContractId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_BuildingManagementContracts_BuildingManagementContracts_Ren~",
                table: "BuildingManagementContracts",
                column: "RenewedFromContractId",
                principalTable: "BuildingManagementContracts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BuildingManagementContracts_BuildingManagementContracts_Ren~",
                table: "BuildingManagementContracts");

            migrationBuilder.DropIndex(
                name: "IX_BuildingManagementContracts_RenewedFromContractId",
                table: "BuildingManagementContracts");

            migrationBuilder.DropColumn(
                name: "RenewedFromContractId",
                table: "BuildingManagementContracts");
        }
    }
}
