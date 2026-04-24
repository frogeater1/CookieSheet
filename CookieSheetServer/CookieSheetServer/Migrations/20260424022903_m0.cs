using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CookieSheetServer.Migrations
{
    /// <inheritdoc />
    public partial class m0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user",
                columns: table => new
                {
                    userId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tel = table.Column<string>(type: "text", nullable: false),
                    qq = table.Column<string>(type: "text", nullable: false),
                    cn = table.Column<string>(type: "text", nullable: false),
                    receiver = table.Column<string>(type: "text", nullable: false),
                    account = table.Column<string>(type: "text", nullable: false),
                    price = table.Column<decimal>(type: "numeric", nullable: false),
                    address = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user", x => x.userId);
                });

            migrationBuilder.CreateTable(
                name: "order",
                columns: table => new
                {
                    orderId = table.Column<string>(type: "text", nullable: false),
                    userId = table.Column<int>(type: "integer", nullable: false),
                    tel = table.Column<string>(type: "text", nullable: false),
                    OrderedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    productName = table.Column<string[]>(type: "text[]", nullable: false),
                    productModel = table.Column<string[]>(type: "text[]", nullable: false),
                    productCount = table.Column<int[]>(type: "integer[]", nullable: false),
                    price = table.Column<decimal>(type: "numeric", nullable: false),
                    sended = table.Column<bool>(type: "boolean", nullable: false),
                    tag = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order", x => x.orderId);
                    table.ForeignKey(
                        name: "FK_order_user_userId",
                        column: x => x.userId,
                        principalTable: "user",
                        principalColumn: "userId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_order_userId",
                table: "order",
                column: "userId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order");

            migrationBuilder.DropTable(
                name: "user");
        }
    }
}
