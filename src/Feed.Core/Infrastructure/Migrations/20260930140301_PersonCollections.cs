using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Feed.Core.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PersonCollections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Person",
                table: "RunRequests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PersonAuthorId",
                table: "RunRequests",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Person",
                table: "RunRequests");

            migrationBuilder.DropColumn(
                name: "PersonAuthorId",
                table: "RunRequests");
        }
    }
}
