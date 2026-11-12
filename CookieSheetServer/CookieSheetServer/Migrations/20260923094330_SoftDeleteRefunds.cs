using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CookieSheetServer.Migrations
{
    /// <inheritdoc />
    public partial class SoftDeleteRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "isDeleted",
                table: "refunds",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "isDeleted",
                table: "refunds");
        }
    }
}
