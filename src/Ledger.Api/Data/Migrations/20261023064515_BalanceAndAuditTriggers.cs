using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BalanceAndAuditTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            using var r = typeof(BalanceAndAuditTriggers).Assembly
                .GetManifestResourceStream("Ledger.Api.Data.Sql.balance_trigger.sql")!;
            migrationBuilder.Sql(new StreamReader(r).ReadToEnd());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS audit_log_no_change ON "AuditLog";
                DROP FUNCTION IF EXISTS audit_log_immutable();
                DROP TRIGGER IF EXISTS journal_lines_balanced ON "JournalLines";
                DROP FUNCTION IF EXISTS check_entry_balanced();
                """);
        }
    }
}
