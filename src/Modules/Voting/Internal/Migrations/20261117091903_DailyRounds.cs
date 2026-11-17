using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LunchSuggest.Modules.Voting.Internal.Migrations
{
    /// <inheritdoc />
    public partial class DailyRounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Candidates_SessionId_PlaceId_Dish",
                schema: "voting",
                table: "Candidates");

            migrationBuilder.AddColumn<DateOnly>(
                name: "SeededOn",
                schema: "voting",
                table: "Sessions",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                schema: "voting",
                table: "Sessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateOnly>(
                name: "Day",
                schema: "voting",
                table: "Candidates",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<string>(
                name: "PickedBy",
                schema: "voting",
                table: "Candidates",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            // Keep existing data: each option belongs to the day it was added; the group's pick moves onto the option itself.
            migrationBuilder.Sql("""UPDATE voting."Candidates" SET "Day" = ("AddedAt" AT TIME ZONE 'UTC')::date;""");
            migrationBuilder.Sql("""
                UPDATE voting."Candidates" c SET "PickedBy" = s."PickedBy"
                FROM voting."Sessions" s WHERE s."PickedCandidateId" = c."Id";
                """);

            migrationBuilder.DropColumn(
                name: "PickedBy",
                schema: "voting",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "PickedCandidateId",
                schema: "voting",
                table: "Sessions");

            migrationBuilder.CreateIndex(
                name: "IX_Candidates_SessionId_Day_PlaceId_Dish",
                schema: "voting",
                table: "Candidates",
                columns: new[] { "SessionId", "Day", "PlaceId", "Dish" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Candidates_SessionId_Day_PlaceId_Dish",
                schema: "voting",
                table: "Candidates");

            migrationBuilder.DropColumn(
                name: "SeededOn",
                schema: "voting",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "TimeZone",
                schema: "voting",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "Day",
                schema: "voting",
                table: "Candidates");

            migrationBuilder.DropColumn(
                name: "PickedBy",
                schema: "voting",
                table: "Candidates");

            migrationBuilder.AddColumn<string>(
                name: "PickedBy",
                schema: "voting",
                table: "Sessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PickedCandidateId",
                schema: "voting",
                table: "Sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Candidates_SessionId_PlaceId_Dish",
                schema: "voting",
                table: "Candidates",
                columns: new[] { "SessionId", "PlaceId", "Dish" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
