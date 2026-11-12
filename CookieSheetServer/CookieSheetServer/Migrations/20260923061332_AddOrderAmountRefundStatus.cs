using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CookieSheetServer.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderAmountRefundStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "orderRefundStatus",
                table: "order",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "receivableAmount",
                table: "order",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "orderRefundStatus",
                table: "order");

            migrationBuilder.DropColumn(
                name: "receivableAmount",
                table: "order");
        }
    }
}
