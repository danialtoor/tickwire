using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tickwire.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveRetentionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_session_messages_IsStore_Timestamp",
                table: "session_messages",
                columns: new[] { "IsStore", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_session_messages_IsStore_Timestamp",
                table: "session_messages");
        }
    }
}
