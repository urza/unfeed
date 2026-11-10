using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Feed.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveCaptureRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaptureRecoveryAttempts");

            migrationBuilder.DropIndex(
                name: "IX_RawSnapshots_RecoveryKey",
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
                name: "ClassificationJson",
                table: "RawSnapshots");

            migrationBuilder.DropColumn(
                name: "RecoveryAttemptedAt",
                table: "RawSnapshots");

            migrationBuilder.DropColumn(
                name: "RecoveryAttempts",
                table: "RawSnapshots");

            migrationBuilder.DropColumn(
                name: "RecoveryError",
                table: "RawSnapshots");

            migrationBuilder.DropColumn(
                name: "RecoveryKey",
                table: "RawSnapshots");

            migrationBuilder.DropColumn(
                name: "RecoveryState",
                table: "RawSnapshots");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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
                name: "ClassificationJson",
                table: "RawSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecoveryAttemptedAt",
                table: "RawSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecoveryAttempts",
                table: "RawSnapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryError",
                table: "RawSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryKey",
                table: "RawSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryState",
                table: "RawSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CaptureRecoveryAttempts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BodyHash = table.Column<string>(type: "TEXT", nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    InputHash = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    PromptVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ProposalJson = table.Column<string>(type: "TEXT", nullable: true),
                    RawSnapshotId = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestJson = table.Column<string>(type: "TEXT", nullable: true),
                    Response = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Validation = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaptureRecoveryAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaptureRecoveryAttempts_RawSnapshots_RawSnapshotId",
                        column: x => x.RawSnapshotId,
                        principalTable: "RawSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RawSnapshots_RecoveryKey",
                table: "RawSnapshots",
                column: "RecoveryKey");

            migrationBuilder.CreateIndex(
                name: "IX_CaptureRecoveryAttempts_RawSnapshotId_StartedAt",
                table: "CaptureRecoveryAttempts",
                columns: new[] { "RawSnapshotId", "StartedAt" });
        }
    }
}
