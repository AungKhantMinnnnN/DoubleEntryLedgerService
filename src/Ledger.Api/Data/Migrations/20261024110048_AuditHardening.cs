using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            using var r = typeof(AuditHardening).Assembly.GetManifestResourceStream("Ledger.Api.Data.Sql.audit_hardening.sql")!;
            migrationBuilder.Sql(new StreamReader(r).ReadToEnd());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TRIGGER IF EXISTS audit_log_no_truncate ON "AuditLog";""");
        }
    }
}
