using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalApp.Migrations
{
    /// <inheritdoc />
    public partial class CamPatientNameEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClinicPatients_ClinicId_CnpHashKey",
                table: "ClinicPatients");

            migrationBuilder.DropColumn(
                name: "CnpEncrypted",
                table: "ClinicPatients");

            migrationBuilder.DropColumn(
                name: "CnpHashKey",
                table: "ClinicPatients");

            migrationBuilder.AddColumn<string>(
                name: "NameKey",
                table: "ClinicPatients",
                type: "nvarchar(220)",
                maxLength: 220,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicPatients_ClinicId_NameKey_Email",
                table: "ClinicPatients",
                columns: new[] { "ClinicId", "NameKey", "Email" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClinicPatients_ClinicId_NameKey_Email",
                table: "ClinicPatients");

            migrationBuilder.DropColumn(
                name: "NameKey",
                table: "ClinicPatients");

            migrationBuilder.AddColumn<string>(
                name: "CnpEncrypted",
                table: "ClinicPatients",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CnpHashKey",
                table: "ClinicPatients",
                type: "nvarchar(13)",
                maxLength: 13,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicPatients_ClinicId_CnpHashKey",
                table: "ClinicPatients",
                columns: new[] { "ClinicId", "CnpHashKey" },
                unique: true);
        }
    }
}
