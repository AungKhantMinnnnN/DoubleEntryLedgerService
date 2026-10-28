using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkLinesToEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JournalLines_JournalEntries_JournalEntryId",
                table: "JournalLines");

            migrationBuilder.DropIndex(
                name: "IX_JournalLines_JournalEntryId",
                table: "JournalLines");

            migrationBuilder.DropColumn(
                name: "JournalEntryId",
                table: "JournalLines");

            migrationBuilder.CreateIndex(
                name: "IX_JournalLines_EntryId",
                table: "JournalLines",
                column: "EntryId");

            migrationBuilder.AddForeignKey(
                name: "FK_JournalLines_JournalEntries_EntryId",
                table: "JournalLines",
                column: "EntryId",
                principalTable: "JournalEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JournalLines_JournalEntries_EntryId",
                table: "JournalLines");

            migrationBuilder.DropIndex(
                name: "IX_JournalLines_EntryId",
                table: "JournalLines");

            migrationBuilder.AddColumn<Guid>(
                name: "JournalEntryId",
                table: "JournalLines",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalLines_JournalEntryId",
                table: "JournalLines",
                column: "JournalEntryId");

            migrationBuilder.AddForeignKey(
                name: "FK_JournalLines_JournalEntries_JournalEntryId",
                table: "JournalLines",
                column: "JournalEntryId",
                principalTable: "JournalEntries",
                principalColumn: "Id");
        }
    }
}
