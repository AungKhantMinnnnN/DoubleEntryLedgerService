using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ledger.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class FxLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OriginalCurrency",
                table: "JournalLines",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_FxRates_Base_Quote_EffectiveAt",
                table: "FxRates",
                columns: new[] { "Base", "Quote", "EffectiveAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FxRates_Base_Quote_EffectiveAt",
                table: "FxRates");

            migrationBuilder.DropColumn(
                name: "OriginalCurrency",
                table: "JournalLines");
        }
    }
}
