using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReversalLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_ReversesEntryId",
                table: "JournalEntries",
                column: "ReversesEntryId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_JournalEntries_JournalEntries_ReversesEntryId",
                table: "JournalEntries",
                column: "ReversesEntryId",
                principalTable: "JournalEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JournalEntries_JournalEntries_ReversesEntryId",
                table: "JournalEntries");

            migrationBuilder.DropIndex(
                name: "IX_JournalEntries_ReversesEntryId",
                table: "JournalEntries");
        }
    }
}
