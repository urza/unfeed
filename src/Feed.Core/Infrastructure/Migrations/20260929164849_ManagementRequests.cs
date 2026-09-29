using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Feed.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ManagementRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RunRequests_Kind_Platform",
                table: "RunRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RequestScope",
                table: "RunRequests");

            migrationBuilder.CreateIndex(
                name: "IX_RunRequests_Kind_Platform",
                table: "RunRequests",
                columns: new[] { "Kind", "Platform" },
                unique: true,
                filter: "Status IN ('pending','claimed') AND Kind IN ('collect','like','friends','login')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RequestScope",
                table: "RunRequests",
                sql: "(Kind = 'process' AND Platform IS NULL) OR (Kind IN ('collect','like','friends','login') AND Platform IN ('facebook','instagram'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RunRequests_Kind_Platform",
                table: "RunRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RequestScope",
                table: "RunRequests");

            migrationBuilder.CreateIndex(
                name: "IX_RunRequests_Kind_Platform",
                table: "RunRequests",
                columns: new[] { "Kind", "Platform" },
                unique: true,
                filter: "Status IN ('pending','claimed') AND Kind IN ('collect','like')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RequestScope",
                table: "RunRequests",
                sql: "(Kind = 'process' AND Platform IS NULL) OR (Kind IN ('collect','like') AND Platform IN ('facebook','instagram'))");
        }
    }
}
