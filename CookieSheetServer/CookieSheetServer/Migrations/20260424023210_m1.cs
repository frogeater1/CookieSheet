using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CookieSheetServer.Migrations
{
    /// <inheritdoc />
    public partial class m1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "OrderedAt",
                table: "order",
                newName: "orderedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "orderedAt",
                table: "order",
                newName: "OrderedAt");
        }
    }
}
