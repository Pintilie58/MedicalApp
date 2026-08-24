using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalApp.Migrations
{
    /// <inheritdoc />
    public partial class AddInterpretationStageTimings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DurationMs",
                table: "InterpretationHistories",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StageTimingsJson",
                table: "InterpretationHistories",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "InterpretationHistories");

            migrationBuilder.DropColumn(
                name: "StageTimingsJson",
                table: "InterpretationHistories");
        }
    }
}
