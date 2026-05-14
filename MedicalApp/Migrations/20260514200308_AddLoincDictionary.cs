using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLoincDictionary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LoincDictionary",
                columns: table => new
                {
                    LoincCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LongCommonName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    OrderObs = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    AliasesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TranslationsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ImportedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoincDictionary", x => x.LoincCode);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoincDictionary_LongCommonName",
                table: "LoincDictionary",
                column: "LongCommonName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoincDictionary");
        }
    }
}
