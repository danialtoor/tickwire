using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tickwire.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PortfolioLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "MaxAbsDelta",
                table: "risk_limits",
                type: "double",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "MaxAbsVega",
                table: "risk_limits",
                type: "double",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxAbsDelta",
                table: "risk_limits");

            migrationBuilder.DropColumn(
                name: "MaxAbsVega",
                table: "risk_limits");
        }
    }
}
