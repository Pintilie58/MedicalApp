using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalApp.Migrations
{
    /// <inheritdoc />
    public partial class AddScaleOutIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Purchases_PurchasedAt",
                table: "Purchases");

            migrationBuilder.DropIndex(
                name: "IX_InterpretationHistories_User_Profile_Status",
                table: "InterpretationHistories");

            migrationBuilder.DropIndex(
                name: "IX_InterpretationHistories_UserEmail",
                table: "InterpretationHistories");

            migrationBuilder.DropIndex(
                name: "IX_ClinicAnalyses_ClinicId",
                table: "ClinicAnalyses");

            migrationBuilder.DropIndex(
                name: "IX_AiUsageLogs_CreatedAt",
                table: "AiUsageLogs");

            migrationBuilder.DropIndex(
                name: "IX_AiUsageLogs_Source",
                table: "AiUsageLogs");

            migrationBuilder.DropIndex(
                name: "IX_AiUsageLogs_Status",
                table: "AiUsageLogs");

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_PurchasedAt",
                table: "Purchases",
                column: "PurchasedAt")
                .Annotation("SqlServer:Include", new[] { "AmountEur" });

            migrationBuilder.CreateIndex(
                name: "IX_InterpretationHistories_Profile_Status",
                table: "InterpretationHistories",
                columns: new[] { "ProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_InterpretationHistories_Status_Id_Desc",
                table: "InterpretationHistories",
                columns: new[] { "Status", "Id" },
                descending: new[] { false, true })
                .Annotation("SqlServer:Include", new[] { "DurationMs" });

            migrationBuilder.CreateIndex(
                name: "IX_InterpretationHistories_User_Id_Desc",
                table: "InterpretationHistories",
                columns: new[] { "UserEmail", "Id" },
                descending: new[] { false, true })
                .Annotation("SqlServer:Include", new[] { "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_InterpretationHistories_User_Profile_Status",
                table: "InterpretationHistories",
                columns: new[] { "UserEmail", "ProfileId", "Status", "CreatedAt" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicAnalyses_Clinic_ProcessedAt",
                table: "ClinicAnalyses",
                columns: new[] { "ClinicId", "ProcessedAt" })
                .Annotation("SqlServer:Include", new[] { "PatientId" });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageLogs_CreatedAt_Status",
                table: "AiUsageLogs",
                columns: new[] { "CreatedAt", "Status" })
                .Annotation("SqlServer:Include", new[] { "Source", "ModelUsed", "InputTokens", "OutputTokens" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Purchases_PurchasedAt",
                table: "Purchases");

            migrationBuilder.DropIndex(
                name: "IX_InterpretationHistories_Profile_Status",
                table: "InterpretationHistories");

            migrationBuilder.DropIndex(
                name: "IX_InterpretationHistories_Status_Id_Desc",
                table: "InterpretationHistories");

            migrationBuilder.DropIndex(
                name: "IX_InterpretationHistories_User_Id_Desc",
                table: "InterpretationHistories");

            migrationBuilder.DropIndex(
                name: "IX_InterpretationHistories_User_Profile_Status",
                table: "InterpretationHistories");

            migrationBuilder.DropIndex(
                name: "IX_ClinicAnalyses_Clinic_ProcessedAt",
                table: "ClinicAnalyses");

            migrationBuilder.DropIndex(
                name: "IX_AiUsageLogs_CreatedAt_Status",
                table: "AiUsageLogs");

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_PurchasedAt",
                table: "Purchases",
                column: "PurchasedAt");

            migrationBuilder.CreateIndex(
                name: "IX_InterpretationHistories_User_Profile_Status",
                table: "InterpretationHistories",
                columns: new[] { "UserEmail", "ProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_InterpretationHistories_UserEmail",
                table: "InterpretationHistories",
                column: "UserEmail");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicAnalyses_ClinicId",
                table: "ClinicAnalyses",
                column: "ClinicId");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageLogs_CreatedAt",
                table: "AiUsageLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageLogs_Source",
                table: "AiUsageLogs",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageLogs_Status",
                table: "AiUsageLogs",
                column: "Status");
        }
    }
}
