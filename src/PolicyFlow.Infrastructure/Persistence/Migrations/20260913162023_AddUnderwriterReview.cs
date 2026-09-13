using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PolicyFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUnderwriterReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReviewNote",
                table: "Quotes",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewedBy",
                table: "Quotes",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewedUtc",
                table: "Quotes",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReviewNote",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "ReviewedBy",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "ReviewedUtc",
                table: "Quotes");
        }
    }
}
