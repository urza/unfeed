using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Feed.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CollectionRecoveryReliability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "VisualAttemptedAt",
                table: "TimelineVisits",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VisualAttempts",
                table: "TimelineVisits",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "VisualDiagnosisJson",
                table: "TimelineVisits",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VisualError",
                table: "TimelineVisits",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CaptureToken",
                table: "RawSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimelineVisits_CaptureToken",
                table: "TimelineVisits",
                column: "CaptureToken");

            migrationBuilder.CreateIndex(
                name: "IX_RawSnapshots_CaptureToken",
                table: "RawSnapshots",
                column: "CaptureToken");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TimelineVisits_CaptureToken",
                table: "TimelineVisits");

            migrationBuilder.DropIndex(
                name: "IX_RawSnapshots_CaptureToken",
                table: "RawSnapshots");

            migrationBuilder.DropColumn(
                name: "VisualAttemptedAt",
                table: "TimelineVisits");

            migrationBuilder.DropColumn(
                name: "VisualAttempts",
                table: "TimelineVisits");

            migrationBuilder.DropColumn(
                name: "VisualDiagnosisJson",
                table: "TimelineVisits");

            migrationBuilder.DropColumn(
                name: "VisualError",
                table: "TimelineVisits");

            migrationBuilder.DropColumn(
                name: "CaptureToken",
                table: "RawSnapshots");
        }
    }
}
