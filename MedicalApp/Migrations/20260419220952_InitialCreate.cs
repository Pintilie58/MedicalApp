using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MedicalApp.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Parola = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Credite = table.Column<int>(type: "int", nullable: false),
                    DataC = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreditConsum = table.Column<int>(type: "int", nullable: false),
                    CreditRest = table.Column<int>(type: "int", nullable: false),
                    PasswordResetToken = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    PasswordResetTokenExpiry = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Email);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
