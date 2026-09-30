using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tickwire.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExecutionVenue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Commission",
                table: "executions",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastMkt",
                table: "executions",
                type: "varchar(8)",
                maxLength: 8,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Commission",
                table: "executions");

            migrationBuilder.DropColumn(
                name: "LastMkt",
                table: "executions");
        }
    }
}
