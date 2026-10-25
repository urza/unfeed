using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Feed.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FeedReadIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_AuthorId",
                table: "Posts");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_AuthorHistory",
                table: "Posts",
                columns: new[] { "AuthorId", "PostedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_FeedMembership",
                table: "Posts",
                columns: new[] { "Platform", "Hidden", "PostedAt", "IsSponsored", "ContentRevision", "VerdictContentRevision", "CategoriesJson" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_AuthorHistory",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_Posts_FeedMembership",
                table: "Posts");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_AuthorId",
                table: "Posts",
                column: "AuthorId");
        }
    }
}
