using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Feed.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProcessingQueues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_RawSnapshots_Platform_AttemptedAt_CapturedAt",
                table: "RawSnapshots",
                columns: new[] { "Platform", "AttemptedAt", "CapturedAt" },
                filter: "Parsed = 0 AND Deleted = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_JudgeQueue",
                table: "Posts",
                columns: new[] { "Platform", "LlmAttemptedAt", "CapturedAt" },
                filter: "IngestReadyAt IS NOT NULL AND (Hidden = 0 OR HiddenBy = 'llm') AND (CategoriesJson IS NULL OR VerdictContentRevision IS NULL OR VerdictContentRevision <> ContentRevision)");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_SummaryQueue",
                table: "Posts",
                columns: new[] { "Platform", "SummaryAttemptedAt", "CapturedAt" },
                filter: "IngestReadyAt IS NOT NULL AND Hidden = 0 AND (Summary IS NULL OR SummaryContentRevision IS NULL OR SummaryContentRevision <> ContentRevision)");

            migrationBuilder.CreateIndex(
                name: "IX_Media_Kind_IsCurrent_AttemptedAt_CreatedAt",
                table: "Media",
                columns: new[] { "Kind", "IsCurrent", "AttemptedAt", "CreatedAt" },
                filter: "Path IS NULL AND PrunedAt IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RawSnapshots_Platform_AttemptedAt_CapturedAt",
                table: "RawSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_Posts_JudgeQueue",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_Posts_SummaryQueue",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_Media_Kind_IsCurrent_AttemptedAt_CreatedAt",
                table: "Media");
        }
    }
}
