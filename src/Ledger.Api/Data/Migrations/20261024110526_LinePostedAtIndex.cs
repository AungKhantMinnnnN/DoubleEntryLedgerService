using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinePostedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PostedAt",
                table: "JournalLines",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Existing lines take their entry's timestamp (the column default above is a placeholder).
            migrationBuilder.Sql("""UPDATE "JournalLines" l SET "PostedAt" = e."PostedAt" FROM "JournalEntries" e WHERE e."Id" = l."EntryId";""");

            migrationBuilder.CreateIndex(
                name: "IX_JournalLines_AccountId_PostedAt_Id",
                table: "JournalLines",
                columns: new[] { "AccountId", "PostedAt", "Id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JournalLines_AccountId_PostedAt_Id",
                table: "JournalLines");

            migrationBuilder.DropColumn(
                name: "PostedAt",
                table: "JournalLines");
        }
    }
}
