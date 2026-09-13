using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PolicyFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuoteSubmittedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SubmittedBy",
                table: "Quotes",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_SubmittedBy_CreatedUtc",
                table: "Quotes",
                columns: new[] { "SubmittedBy", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Quotes_SubmittedBy_CreatedUtc",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "SubmittedBy",
                table: "Quotes");
        }
    }
}
