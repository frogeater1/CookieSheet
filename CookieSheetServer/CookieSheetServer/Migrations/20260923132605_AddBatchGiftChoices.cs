using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CookieSheetServer.Migrations
{
    /// <inheritdoc />
    public partial class AddBatchGiftChoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "giftChoices",
                columns: table => new
                {
                    Batch = table.Column<string>(type: "text", nullable: false),
                    FullGift = table.Column<bool>(type: "boolean", nullable: false),
                    SetGift = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_giftChoices", x => x.Batch);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "giftChoices");
        }
    }
}
