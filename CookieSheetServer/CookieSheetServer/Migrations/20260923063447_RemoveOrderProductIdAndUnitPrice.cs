using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CookieSheetServer.Migrations
{
    /// <inheritdoc />
    public partial class RemoveOrderProductIdAndUnitPrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "productId",
                table: "order");

            migrationBuilder.DropColumn(
                name: "productUnitPrice",
                table: "order");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "productId",
                table: "order",
                type: "text[]",
                nullable: false,
                defaultValueSql: "ARRAY[]::text[]");

            migrationBuilder.AddColumn<decimal[]>(
                name: "productUnitPrice",
                table: "order",
                type: "numeric[]",
                nullable: false,
                defaultValueSql: "ARRAY[]::numeric[]");
        }
    }
}
