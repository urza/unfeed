using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Feed.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class IdentityEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IdentityEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Platform = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    ObservedId = table.Column<string>(type: "TEXT", nullable: true),
                    KeptAuthorId = table.Column<long>(type: "INTEGER", nullable: false),
                    RemovedAuthorId = table.Column<long>(type: "INTEGER", nullable: true),
                    DetailJson = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityEvents_Platform_Key_ObservedId",
                table: "IdentityEvents",
                columns: new[] { "Platform", "Key", "ObservedId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdentityEvents");
        }
    }
}
