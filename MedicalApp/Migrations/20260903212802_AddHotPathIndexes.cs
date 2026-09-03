using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalApp.Migrations
{
    /// <inheritdoc />
    public partial class AddHotPathIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_InterpretationHistories_User_PdfSha256",
                table: "InterpretationHistories",
                columns: new[] { "UserEmail", "PdfSha256" });

            migrationBuilder.CreateIndex(
                name: "IX_InterpretationHistories_User_Profile_Status",
                table: "InterpretationHistories",
                columns: new[] { "UserEmail", "ProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicAnalyses_Clinic_Patient",
                table: "ClinicAnalyses",
                columns: new[] { "ClinicId", "PatientId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InterpretationHistories_User_PdfSha256",
                table: "InterpretationHistories");

            migrationBuilder.DropIndex(
                name: "IX_InterpretationHistories_User_Profile_Status",
                table: "InterpretationHistories");

            migrationBuilder.DropIndex(
                name: "IX_ClinicAnalyses_Clinic_Patient",
                table: "ClinicAnalyses");
        }
    }
}
