using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Feed.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CaptureClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CaptureToken",
                table: "TimelineVisits",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClassificationJson",
                table: "RawSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContextJson",
                table: "RawSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ParserVersion",
                table: "RawSnapshots",
                type: "INTEGER",
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
                    RawSnapshotId = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    BodyHash = table.Column<string>(type: "TEXT", nullable: false),
                    InputHash = table.Column<string>(type: "TEXT", nullable: false),
                    Response = table.Column<string>(type: "TEXT", nullable: true),
                    RequestJson = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", nullable: true),
                    PromptVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ProposalJson = table.Column<string>(type: "TEXT", nullable: true),
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

            migrationBuilder.CreateTable(
                name: "RawDiagnostics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RawSnapshotId = table.Column<long>(type: "INTEGER", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Impact = table.Column<string>(type: "TEXT", nullable: false),
                    Evidence = table.Column<string>(type: "TEXT", nullable: false),
                    Root = table.Column<int>(type: "INTEGER", nullable: false),
                    ParserVersion = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RawDiagnostics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RawDiagnostics_RawSnapshots_RawSnapshotId",
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

            migrationBuilder.CreateIndex(
                name: "IX_RawDiagnostics_Fingerprint_RawSnapshotId",
                table: "RawDiagnostics",
                columns: new[] { "Fingerprint", "RawSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_RawDiagnostics_RawSnapshotId",
                table: "RawDiagnostics",
                column: "RawSnapshotId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaptureRecoveryAttempts");

            migrationBuilder.DropTable(
                name: "RawDiagnostics");

            migrationBuilder.DropIndex(
                name: "IX_RawSnapshots_RecoveryKey",
                table: "RawSnapshots");

            migrationBuilder.DropColumn(
                name: "CaptureToken",
                table: "TimelineVisits");

            migrationBuilder.DropColumn(
                name: "ClassificationJson",
                table: "RawSnapshots");

            migrationBuilder.DropColumn(
                name: "ContextJson",
                table: "RawSnapshots");

            migrationBuilder.DropColumn(
                name: "ParserVersion",
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
    }
}
