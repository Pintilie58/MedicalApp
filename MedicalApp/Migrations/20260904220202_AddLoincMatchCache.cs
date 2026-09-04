using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLoincMatchCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LoincMatchCache",
                columns: table => new
                {
                    CacheKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TestName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PipelineVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LoincCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LongName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    LoincClass = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    LoincSource = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Score = table.Column<double>(type: "float", nullable: false),
                    AxisVerdictJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    HitCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoincMatchCache", x => x.CacheKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoincMatchCache_PipelineVersion",
                table: "LoincMatchCache",
                column: "PipelineVersion");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoincMatchCache");
        }
    }
}
