using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ambev.DeveloperEvaluation.ORM.Migrations
{
    /// <summary>
    /// Enables the Postgres extensions the schema depends on:
    ///   - pgcrypto: provides gen_random_uuid() so future migrations or
    ///     manual inserts can generate UUIDs server-side without surprises
    ///     on environments where the extension wasn't pre-enabled.
    ///   - pg_trgm: provides trigram operators used by the GIN index on
    ///     Sales.SaleNumber for substring (ILIKE %foo%) lookups.
    ///
    /// Adds a GIN trigram index on Sales.SaleNumber so the list endpoint's
    /// substring search can use it instead of falling back to a sequential
    /// scan when the wildcard is not anchored.
    /// </summary>
    public partial class EnableExtensionsAndTrigramIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"CREATE EXTENSION IF NOT EXISTS pgcrypto;");
            migrationBuilder.Sql(@"CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_Sales_SaleNumber_Trgm""
                ON ""Sales"" USING gin (""SaleNumber"" gin_trgm_ops);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Sales_SaleNumber_Trgm"";");
            // Extensions are intentionally NOT dropped on Down — other apps
            // sharing the cluster may depend on them.
        }
    }
}
