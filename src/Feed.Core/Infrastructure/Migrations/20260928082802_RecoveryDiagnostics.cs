using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Feed.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RecoveryDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "SweepTargets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "RetryAt",
                table: "SweepTargets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RetryIncomplete",
                table: "RunRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "BlockedParserVersion",
                table: "RawSnapshots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Warning",
                table: "RawSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LlmError",
                table: "Posts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LlmFailures",
                table: "Posts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SummaryError",
                table: "Posts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SummaryFailures",
                table: "Posts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "SweepTargets");

            migrationBuilder.DropColumn(
                name: "RetryAt",
                table: "SweepTargets");

            migrationBuilder.DropColumn(
                name: "RetryIncomplete",
                table: "RunRequests");

            migrationBuilder.DropColumn(
                name: "BlockedParserVersion",
                table: "RawSnapshots");

            migrationBuilder.DropColumn(
                name: "Warning",
                table: "RawSnapshots");

            migrationBuilder.DropColumn(
                name: "LlmError",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "LlmFailures",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "SummaryError",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "SummaryFailures",
                table: "Posts");
        }
    }
}
