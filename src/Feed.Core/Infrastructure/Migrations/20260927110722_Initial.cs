using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Feed.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Authors",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Platform = table.Column<string>(type: "TEXT", nullable: false),
                    PlatformAuthorId = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    IsFriend = table.Column<bool>(type: "INTEGER", nullable: false),
                    AvatarPath = table.Column<string>(type: "TEXT", nullable: true),
                    RefsJson = table.Column<string>(type: "TEXT", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Authors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Feedback",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PostId = table.Column<long>(type: "INTEGER", nullable: false),
                    Value = table.Column<int>(type: "INTEGER", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Platform = table.Column<string>(type: "TEXT", nullable: true),
                    AuthorId = table.Column<long>(type: "INTEGER", nullable: true),
                    ViewKey = table.Column<string>(type: "TEXT", nullable: true),
                    Scope = table.Column<string>(type: "TEXT", nullable: true),
                    CategoriesAtVote = table.Column<string>(type: "TEXT", nullable: true),
                    ScoreAtVote = table.Column<int>(type: "INTEGER", nullable: true),
                    ReasonAtVote = table.Column<string>(type: "TEXT", nullable: true),
                    Curated = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Feedback", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Kv",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Kv", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Likes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PostId = table.Column<long>(type: "INTEGER", nullable: false),
                    Platform = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AttemptedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Likes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformStates",
                columns: table => new
                {
                    Platform = table.Column<string>(type: "TEXT", nullable: false),
                    NeedsRelogin = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastOkRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRunFinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformStates", x => x.Platform);
                });

            migrationBuilder.CreateTable(
                name: "RawSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Platform = table.Column<string>(type: "TEXT", nullable: false),
                    RunId = table.Column<long>(type: "INTEGER", nullable: true),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Parsed = table.Column<bool>(type: "INTEGER", nullable: false),
                    AttemptedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ParsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RawSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunRequests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Platform = table.Column<string>(type: "TEXT", nullable: true),
                    Mode = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClaimToken = table.Column<string>(type: "TEXT", nullable: true),
                    ChildPid = table.Column<int>(type: "INTEGER", nullable: true),
                    ChildStartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunRequests", x => x.Id);
                    table.CheckConstraint("CK_RequestScope", "(Kind = 'process' AND Platform IS NULL) OR (Kind IN ('collect','like') AND Platform IN ('facebook','instagram'))");
                });

            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Platform = table.Column<string>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: true),
                    Phase = table.Column<string>(type: "TEXT", nullable: false),
                    ProcessPid = table.Column<int>(type: "INTEGER", nullable: false),
                    ProcessStartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RequestId = table.Column<long>(type: "INTEGER", nullable: true),
                    Trigger = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PostsFound = table.Column<int>(type: "INTEGER", nullable: false),
                    PostsNew = table.Column<int>(type: "INTEGER", nullable: false),
                    StatsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    RawDir = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SweepTargets",
                columns: table => new
                {
                    Platform = table.Column<string>(type: "TEXT", nullable: false),
                    CycleId = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorId = table.Column<long>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    RunId = table.Column<long>(type: "INTEGER", nullable: true),
                    VisitId = table.Column<long>(type: "INTEGER", nullable: true),
                    AttemptedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SweepTargets", x => new { x.Platform, x.CycleId, x.AuthorId });
                });

            migrationBuilder.CreateTable(
                name: "TimelineVisits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Platform = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorId = table.Column<long>(type: "INTEGER", nullable: true),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    RunId = table.Column<long>(type: "INTEGER", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CaptureStatus = table.Column<string>(type: "TEXT", nullable: false),
                    PostsFound = table.Column<int>(type: "INTEGER", nullable: false),
                    NewestPostedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    OldestPostedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StopReason = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    Screenshot = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimelineVisits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuthorKeys",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Platform = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthorKeys", x => x.Key);
                    table.ForeignKey(
                        name: "FK_AuthorKeys_Authors_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "Authors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Posts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Platform = table.Column<string>(type: "TEXT", nullable: false),
                    PlatformPostId = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorId = table.Column<long>(type: "INTEGER", nullable: true),
                    ObservedAuthorName = table.Column<string>(type: "TEXT", nullable: true),
                    ObservedAuthorUrl = table.Column<string>(type: "TEXT", nullable: true),
                    PostedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CapturedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RawRef = table.Column<string>(type: "TEXT", nullable: true),
                    LatestRawRef = table.Column<string>(type: "TEXT", nullable: true),
                    MediaManifestJson = table.Column<string>(type: "TEXT", nullable: false),
                    ContentRevision = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    IngestReadyAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Text = table.Column<string>(type: "TEXT", nullable: true),
                    Permalink = table.Column<string>(type: "TEXT", nullable: true),
                    LikeRef = table.Column<string>(type: "TEXT", nullable: true),
                    IsSponsored = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsSuggested = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsReel = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsEvent = table.Column<bool>(type: "INTEGER", nullable: false),
                    MemoryLabel = table.Column<string>(type: "TEXT", nullable: true),
                    MemoryText = table.Column<string>(type: "TEXT", nullable: true),
                    TagsJson = table.Column<string>(type: "TEXT", nullable: true),
                    SharedAuthor = table.Column<string>(type: "TEXT", nullable: true),
                    SharedText = table.Column<string>(type: "TEXT", nullable: true),
                    SharedUrl = table.Column<string>(type: "TEXT", nullable: true),
                    CategoriesJson = table.Column<string>(type: "TEXT", nullable: true),
                    LlmScore = table.Column<int>(type: "INTEGER", nullable: true),
                    LlmReason = table.Column<string>(type: "TEXT", nullable: true),
                    PrefsVersion = table.Column<string>(type: "TEXT", nullable: true),
                    VerdictContentRevision = table.Column<int>(type: "INTEGER", nullable: true),
                    VerdictInputHash = table.Column<string>(type: "TEXT", nullable: true),
                    VerdictModel = table.Column<string>(type: "TEXT", nullable: true),
                    VerdictEndpoint = table.Column<string>(type: "TEXT", nullable: true),
                    JudgedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    SummaryContentRevision = table.Column<int>(type: "INTEGER", nullable: true),
                    SummaryInputHash = table.Column<string>(type: "TEXT", nullable: true),
                    SummaryModel = table.Column<string>(type: "TEXT", nullable: true),
                    SummaryEndpoint = table.Column<string>(type: "TEXT", nullable: true),
                    SummarizedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LlmAttemptedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SummaryAttemptedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Hidden = table.Column<bool>(type: "INTEGER", nullable: false),
                    HiddenBy = table.Column<string>(type: "TEXT", nullable: true),
                    HiddenReason = table.Column<string>(type: "TEXT", nullable: true),
                    HiddenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    VisibilityRevision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Posts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Posts_Authors_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "Authors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Media",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PostId = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Path = table.Column<string>(type: "TEXT", nullable: true),
                    Width = table.Column<int>(type: "INTEGER", nullable: true),
                    Height = table.Column<int>(type: "INTEGER", nullable: true),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: true),
                    IsCurrent = table.Column<bool>(type: "INTEGER", nullable: false),
                    AttemptedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DownloadAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PrunedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Media", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Media_Posts_PostId",
                        column: x => x.PostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuthorKeys_AuthorId",
                table: "AuthorKeys",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_Authors_Platform_IsFriend",
                table: "Authors",
                columns: new[] { "Platform", "IsFriend" });

            migrationBuilder.CreateIndex(
                name: "IX_Authors_Platform_PlatformAuthorId",
                table: "Authors",
                columns: new[] { "Platform", "PlatformAuthorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Feedback_PostId",
                table: "Feedback",
                column: "PostId");

            migrationBuilder.CreateIndex(
                name: "IX_Likes_PostId",
                table: "Likes",
                column: "PostId",
                unique: true,
                filter: "State = 'pending'");

            migrationBuilder.CreateIndex(
                name: "IX_Likes_PostId_State",
                table: "Likes",
                columns: new[] { "PostId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Media_PostId",
                table: "Media",
                column: "PostId");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_AuthorId",
                table: "Posts",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Hidden_PostedAt",
                table: "Posts",
                columns: new[] { "Hidden", "PostedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Platform_PlatformPostId",
                table: "Posts",
                columns: new[] { "Platform", "PlatformPostId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Posts_PostedAt",
                table: "Posts",
                column: "PostedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RawSnapshots_Path",
                table: "RawSnapshots",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RawSnapshots_Platform_CapturedAt",
                table: "RawSnapshots",
                columns: new[] { "Platform", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RunRequests_Kind",
                table: "RunRequests",
                column: "Kind",
                unique: true,
                filter: "Status IN ('pending','claimed') AND Kind = 'process'");

            migrationBuilder.CreateIndex(
                name: "IX_RunRequests_Kind_Platform",
                table: "RunRequests",
                columns: new[] { "Kind", "Platform" },
                unique: true,
                filter: "Status IN ('pending','claimed') AND Kind IN ('collect','like')");

            migrationBuilder.CreateIndex(
                name: "IX_RunRequests_Status_RequestedAt",
                table: "RunRequests",
                columns: new[] { "Status", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_FinishedAt",
                table: "Runs",
                column: "FinishedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_Platform_Status",
                table: "Runs",
                columns: new[] { "Platform", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_RequestId",
                table: "Runs",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_TimelineVisits_Platform_Url_Id",
                table: "TimelineVisits",
                columns: new[] { "Platform", "Url", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthorKeys");

            migrationBuilder.DropTable(
                name: "Feedback");

            migrationBuilder.DropTable(
                name: "Kv");

            migrationBuilder.DropTable(
                name: "Likes");

            migrationBuilder.DropTable(
                name: "Media");

            migrationBuilder.DropTable(
                name: "PlatformStates");

            migrationBuilder.DropTable(
                name: "RawSnapshots");

            migrationBuilder.DropTable(
                name: "RunRequests");

            migrationBuilder.DropTable(
                name: "Runs");

            migrationBuilder.DropTable(
                name: "SweepTargets");

            migrationBuilder.DropTable(
                name: "TimelineVisits");

            migrationBuilder.DropTable(
                name: "Posts");

            migrationBuilder.DropTable(
                name: "Authors");
        }
    }
}
