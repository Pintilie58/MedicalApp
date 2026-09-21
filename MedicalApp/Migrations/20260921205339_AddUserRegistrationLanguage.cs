using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalApp.Migrations
{
    /// <inheritdoc />
    public partial class AddUserRegistrationLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RegistrationLanguage",
                table: "Users",
                type: "nvarchar(5)",
                maxLength: 5,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RegistrationLanguage",
                table: "Users");
        }
    }
}
