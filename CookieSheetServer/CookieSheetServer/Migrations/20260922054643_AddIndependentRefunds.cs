using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CookieSheetServer.Migrations
{
    /// <inheritdoc />
    public partial class AddIndependentRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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

            migrationBuilder.CreateTable(
                name: "refunds",
                columns: table => new
                {
                    refundId = table.Column<string>(type: "text", nullable: false),
                    orderId = table.Column<string>(type: "text", nullable: false),
                    productName = table.Column<string>(type: "text", nullable: false),
                    productModel = table.Column<string>(type: "text", nullable: false),
                    productId = table.Column<string>(type: "text", nullable: false),
                    productCode = table.Column<string>(type: "text", nullable: false),
                    modelCode = table.Column<string>(type: "text", nullable: false),
                    productPaidAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    shippingPaidAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    refundAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    shippingRefundAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    refundType = table.Column<string>(type: "text", nullable: false),
                    saleStage = table.Column<string>(type: "text", nullable: false),
                    initiator = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    shippingStatus = table.Column<string>(type: "text", nullable: false),
                    orderPaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    requestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deadlineAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sourceFile = table.Column<string>(type: "text", nullable: false),
                    sourceRow = table.Column<int>(type: "integer", nullable: false),
                    importedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refunds", x => x.refundId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_refunds_orderId",
                table: "refunds",
                column: "orderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refunds");

            migrationBuilder.DropColumn(
                name: "productId",
                table: "order");

            migrationBuilder.DropColumn(
                name: "productUnitPrice",
                table: "order");
        }
    }
}
