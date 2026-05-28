using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalApp.Migrations
{
    /// <inheritdoc />
    public partial class CamPdfOverridesAndBlacklist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmailDomainBlacklist",
                table: "Clinics",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClinicPdfOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ClinicId = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    OverrideName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OverrideEmail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClinicPdfOverrides", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicPdfOverrides_ClinicId_FileName",
                table: "ClinicPdfOverrides",
                columns: new[] { "ClinicId", "FileName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClinicPdfOverrides");

            migrationBuilder.DropColumn(
                name: "EmailDomainBlacklist",
                table: "Clinics");
        }
    }
}
