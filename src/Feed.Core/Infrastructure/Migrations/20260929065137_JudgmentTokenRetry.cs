using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Feed.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class JudgmentTokenRetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_JudgeQueue",
                table: "Posts");

            migrationBuilder.AddColumn<int>(
                name: "LlmTokenLimit",
                table: "Posts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Posts_JudgeQueue",
                table: "Posts",
                columns: new[] { "Platform", "LlmAttemptedAt", "CapturedAt" },
                filter: "IngestReadyAt IS NOT NULL AND (Hidden = 0 OR HiddenBy = 'llm') AND (CategoriesJson IS NULL OR VerdictContentRevision IS NULL OR VerdictContentRevision <> ContentRevision OR LlmTokenLimit IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_JudgeQueue",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "LlmTokenLimit",
                table: "Posts");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_JudgeQueue",
                table: "Posts",
                columns: new[] { "Platform", "LlmAttemptedAt", "CapturedAt" },
                filter: "IngestReadyAt IS NOT NULL AND (Hidden = 0 OR HiddenBy = 'llm') AND (CategoriesJson IS NULL OR VerdictContentRevision IS NULL OR VerdictContentRevision <> ContentRevision)");
        }
    }
}
