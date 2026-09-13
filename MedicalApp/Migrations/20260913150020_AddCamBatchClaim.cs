using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalApp.Migrations
{
    /// <inheritdoc />
    public partial class AddCamBatchClaim : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LeaseUntil",
                table: "ClinicBatchRuns");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "ClinicBatchRuns");

            migrationBuilder.CreateTable(
                name: "ClinicBatchClaims",
                columns: table => new
                {
                    BatchRunId = table.Column<int>(type: "int", nullable: false),
                    OwnerInstance = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LeaseUntil = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClinicBatchClaims", x => x.BatchRunId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicBatchClaims_LeaseUntil",
                table: "ClinicBatchClaims",
                column: "LeaseUntil");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClinicBatchClaims");

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseUntil",
                table: "ClinicBatchRuns",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "ClinicBatchRuns",
                type: "rowversion",
                rowVersion: true,
                nullable: true);
        }
    }
}
