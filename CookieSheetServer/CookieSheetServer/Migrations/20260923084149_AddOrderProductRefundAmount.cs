using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CookieSheetServer.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderProductRefundAmount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "productRefundAmount",
                table: "order",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "productRefundAmount",
                table: "order");
        }
    }
}
