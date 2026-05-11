using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ambev.DeveloperEvaluation.ORM.Migrations
{
    /// <summary>
    /// Adds an AFTER INSERT statement-level trigger on OutboxMessages that
    /// fires <c>NOTIFY outbox_pending</c>. Statement-level (not per-row) so
    /// a batch insert from a single handler — typically 1-4 events for one
    /// sale — produces a single notification instead of N.
    ///
    /// <see cref="Outbox.OutboxDispatcherService"/> keeps a dedicated LISTEN
    /// connection open and wakes up immediately on the notification,
    /// cutting publish latency from "up to 5 s" (the poll interval) to
    /// sub-second.
    /// </summary>
    public partial class OutboxNotifyTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION ambev_outbox_notify_pending()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM pg_notify('outbox_pending', '');
                    RETURN NULL;
                END;
                $$;
            ");

            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS trg_outbox_notify_pending ON ""OutboxMessages"";
                CREATE TRIGGER trg_outbox_notify_pending
                AFTER INSERT ON ""OutboxMessages""
                FOR EACH STATEMENT
                EXECUTE FUNCTION ambev_outbox_notify_pending();
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS trg_outbox_notify_pending ON ""OutboxMessages"";");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS ambev_outbox_notify_pending();");
        }
    }
}
