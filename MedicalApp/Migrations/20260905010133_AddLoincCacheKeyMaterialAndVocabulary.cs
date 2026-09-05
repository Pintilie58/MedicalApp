using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLoincCacheKeyMaterialAndVocabulary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KeyMaterial",
                table: "LoincMatchCache",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LoincVocabulary",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PhrasesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PhraseCount = table.Column<int>(type: "int", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoincVocabulary", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoincVocabulary");

            migrationBuilder.DropColumn(
                name: "KeyMaterial",
                table: "LoincMatchCache");
        }
    }
}
