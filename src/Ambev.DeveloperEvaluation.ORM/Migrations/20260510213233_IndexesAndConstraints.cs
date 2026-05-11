using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ambev.DeveloperEvaluation.ORM.Migrations
{
    /// <inheritdoc />
    public partial class IndexesAndConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sales_BranchId",
                table: "Sales");

            migrationBuilder.DropIndex(
                name: "IX_Sales_CustomerId",
                table: "Sales");

            migrationBuilder.DropIndex(
                name: "IX_Sales_SaleDate",
                table: "Sales");

            migrationBuilder.DropIndex(
                name: "IX_SaleItems_SaleId_ProductId",
                table: "SaleItems");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_Pending",
                table: "OutboxMessages");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                table: "Sales",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now() at time zone 'utc'",
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sales_BranchId_SaleDate",
                table: "Sales",
                columns: new[] { "BranchId", "SaleDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Sales_CustomerId_SaleDate",
                table: "Sales",
                columns: new[] { "CustomerId", "SaleDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Sales_SaleDate_Cancelled",
                table: "Sales",
                column: "SaleDate",
                filter: "\"IsCancelled\" = true");

            migrationBuilder.CreateIndex(
                name: "UX_SaleItems_SaleId_ProductId_Active",
                table: "SaleItems",
                columns: new[] { "SaleId", "ProductId" },
                unique: true,
                filter: "\"IsCancelled\" = false");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SaleItems_Discount",
                table: "SaleItems",
                sql: "\"Discount\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SaleItems_Quantity",
                table: "SaleItems",
                sql: "\"Quantity\" >= 1 AND \"Quantity\" <= 20");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SaleItems_TotalAmount",
                table: "SaleItems",
                sql: "\"TotalAmount\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SaleItems_UnitPrice",
                table: "SaleItems",
                sql: "\"UnitPrice\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Pending",
                table: "OutboxMessages",
                column: "OccurredAt",
                filter: "\"ProcessedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_Email",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_Username",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Sales_BranchId_SaleDate",
                table: "Sales");

            migrationBuilder.DropIndex(
                name: "IX_Sales_CustomerId_SaleDate",
                table: "Sales");

            migrationBuilder.DropIndex(
                name: "IX_Sales_SaleDate_Cancelled",
                table: "Sales");

            migrationBuilder.DropIndex(
                name: "UX_SaleItems_SaleId_ProductId_Active",
                table: "SaleItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SaleItems_Discount",
                table: "SaleItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SaleItems_Quantity",
                table: "SaleItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SaleItems_TotalAmount",
                table: "SaleItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SaleItems_UnitPrice",
                table: "SaleItems");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_Pending",
                table: "OutboxMessages");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                table: "Sales",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldDefaultValueSql: "now() at time zone 'utc'");

            migrationBuilder.CreateIndex(
                name: "IX_Sales_BranchId",
                table: "Sales",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_Sales_CustomerId",
                table: "Sales",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Sales_SaleDate",
                table: "Sales",
                column: "SaleDate");

            migrationBuilder.CreateIndex(
                name: "IX_SaleItems_SaleId_ProductId",
                table: "SaleItems",
                columns: new[] { "SaleId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Pending",
                table: "OutboxMessages",
                columns: new[] { "ProcessedAt", "OccurredAt" });
        }
    }
}
