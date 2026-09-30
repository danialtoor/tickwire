using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tickwire.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "fix_sessions",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "trading")
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Role",
                table: "fix_sessions");
        }
    }
}
