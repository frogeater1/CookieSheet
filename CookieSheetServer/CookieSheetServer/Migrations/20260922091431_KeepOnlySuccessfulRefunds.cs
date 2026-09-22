using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CookieSheetServer.Migrations
{
    /// <inheritdoc />
    public partial class KeepOnlySuccessfulRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM refunds WHERE btrim("status") <> '退款成功';
                UPDATE refunds SET "status" = btrim("status") WHERE "status" <> btrim("status");
                """);
            migrationBuilder.AddCheckConstraint(
                name: "CK_refunds_successful_status",
                table: "refunds",
                sql: "\"status\" = '退款成功'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Removing the constraint does not restore discarded non-successful records.
            migrationBuilder.DropCheckConstraint(
                name: "CK_refunds_successful_status",
                table: "refunds");
        }
    }
}
