using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ambev.DeveloperEvaluation.ORM.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveItemsCountToSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActiveItemsCount",
                table: "Sales",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill the new column for rows that existed before it did,
            // so the list endpoint matches reality on first deploy.
            migrationBuilder.Sql(@"
                UPDATE ""Sales"" s
                SET ""ActiveItemsCount"" = sub.cnt
                FROM (
                    SELECT ""SaleId"", COUNT(*)::int AS cnt
                    FROM ""SaleItems""
                    WHERE ""IsCancelled"" = false
                    GROUP BY ""SaleId""
                ) AS sub
                WHERE s.""Id"" = sub.""SaleId"";
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActiveItemsCount",
                table: "Sales");
        }
    }
}
