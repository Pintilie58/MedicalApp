using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalApp.Migrations
{
    /// <inheritdoc />
    public partial class AddRawJsonResultToHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RawJsonResult",
                table: "InterpretationHistories",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RawJsonResult",
                table: "InterpretationHistories");
        }
    }
}
