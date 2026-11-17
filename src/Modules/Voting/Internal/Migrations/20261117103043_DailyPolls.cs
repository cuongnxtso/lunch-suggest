using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LunchSuggest.Modules.Voting.Internal.Migrations
{
    /// <inheritdoc />
    public partial class DailyPolls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Polls",
                schema: "voting",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SeededAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Polls", x => new { x.SessionId, x.Day });
                    table.ForeignKey(
                        name: "FK_Polls_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalSchema: "voting",
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Every existing day with options becomes a poll (made by the app, already seeded), so the new key below holds.
            migrationBuilder.Sql("""
                INSERT INTO voting."Polls" ("SessionId", "Day", "CreatedBy", "CreatedAt", "SeededAt")
                SELECT "SessionId", "Day", 'app', min("AddedAt"), min("AddedAt")
                FROM voting."Candidates" GROUP BY "SessionId", "Day";
                """);

            migrationBuilder.DropColumn(
                name: "SeededOn",
                schema: "voting",
                table: "Sessions");

            migrationBuilder.AddForeignKey(
                name: "FK_Candidates_Polls_SessionId_Day",
                schema: "voting",
                table: "Candidates",
                columns: new[] { "SessionId", "Day" },
                principalSchema: "voting",
                principalTable: "Polls",
                principalColumns: new[] { "SessionId", "Day" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Candidates_Polls_SessionId_Day",
                schema: "voting",
                table: "Candidates");

            migrationBuilder.DropTable(
                name: "Polls",
                schema: "voting");

            migrationBuilder.AddColumn<DateOnly>(
                name: "SeededOn",
                schema: "voting",
                table: "Sessions",
                type: "date",
                nullable: true);
        }
    }
}
