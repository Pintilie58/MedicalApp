using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalApp.Migrations
{
    /// <inheritdoc />
    public partial class AddClinicAnalysisPdfSha256 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PdfSha256",
                table: "ClinicAnalyses",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClinicAnalyses_Clinic_PdfSha256",
                table: "ClinicAnalyses",
                columns: new[] { "ClinicId", "PdfSha256" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClinicAnalyses_Clinic_PdfSha256",
                table: "ClinicAnalyses");

            migrationBuilder.DropColumn(
                name: "PdfSha256",
                table: "ClinicAnalyses");
        }
    }
}
