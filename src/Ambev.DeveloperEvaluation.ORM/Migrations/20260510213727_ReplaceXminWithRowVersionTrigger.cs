using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ambev.DeveloperEvaluation.ORM.Migrations
{
    /// <summary>
    /// Replaces the xmin-based optimistic concurrency token on Sales with a
    /// dedicated bigint <c>RowVersion</c> column maintained by a BEFORE
    /// UPDATE trigger.
    /// </summary>
    /// <remarks>
    /// xmin (the Postgres transaction id of the row's last update) is reset
    /// by VACUUM FREEZE when its age crosses vacuum_freeze_table_age (default
    /// 150M txns). On a high-volume table that event invalidates every cached
    /// ETag at once, surfacing as a wave of 412 Precondition Failed responses
    /// for every client that read the row before the freeze.
    ///
    /// A bigint maintained by a trigger has none of that surface area: it is
    /// monotonically incremented per row, lives outside MVCC bookkeeping and
    /// is unaffected by FREEZE.
    /// </remarks>
    public partial class ReplaceXminWithRowVersionTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The previous mapping declared a CLR property mapped to the xmin
            // SYSTEM column. Postgres exposes xmin on every table as a
            // built-in system column (attnum < 0) — even DROP COLUMN IF
            // EXISTS targets it and raises 0A000 "cannot drop system column".
            // Only attempt the drop when a USER column called "xmin" really
            // exists (attnum > 0), which would only happen on an environment
            // that ran an older revision of AddSaleConcurrencyToken.
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                          FROM pg_attribute
                         WHERE attrelid = '""Sales""'::regclass
                           AND attname  = 'xmin'
                           AND attnum   > 0
                           AND NOT attisdropped
                    ) THEN
                        ALTER TABLE ""Sales"" DROP COLUMN ""xmin"";
                    END IF;
                END $$;
            ");

            migrationBuilder.AddColumn<long>(
                name: "RowVersion",
                table: "Sales",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION ambev_sales_bump_rowversion()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    NEW.""RowVersion"" := COALESCE(OLD.""RowVersion"", 0) + 1;
                    RETURN NEW;
                END;
                $$;
            ");

            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS trg_sales_bump_rowversion ON ""Sales"";
                CREATE TRIGGER trg_sales_bump_rowversion
                BEFORE UPDATE ON ""Sales""
                FOR EACH ROW
                EXECUTE FUNCTION ambev_sales_bump_rowversion();
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS trg_sales_bump_rowversion ON ""Sales"";");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS ambev_sales_bump_rowversion();");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Sales");
        }
    }
}
