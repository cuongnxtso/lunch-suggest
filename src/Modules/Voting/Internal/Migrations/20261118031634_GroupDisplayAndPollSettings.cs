using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LunchSuggest.Modules.Voting.Internal.Migrations
{
    /// <inheritdoc />
    public partial class GroupDisplayAndPollSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LandingIcon",
                schema: "voting",
                table: "Sessions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxOptions",
                schema: "voting",
                table: "Sessions",
                type: "integer",
                nullable: false,
                defaultValue: 8);

            migrationBuilder.AddColumn<int>(
                name: "PicksMax",
                schema: "voting",
                table: "Sessions",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<int>(
                name: "PicksMin",
                schema: "voting",
                table: "Sessions",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "RunnerUpDays",
                schema: "voting",
                table: "Sessions",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<string>(
                name: "SiteTitle",
                schema: "voting",
                table: "Sessions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LandingIcon",
                schema: "voting",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "MaxOptions",
                schema: "voting",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "PicksMax",
                schema: "voting",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "PicksMin",
                schema: "voting",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "RunnerUpDays",
                schema: "voting",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "SiteTitle",
                schema: "voting",
                table: "Sessions");
        }
    }
}
