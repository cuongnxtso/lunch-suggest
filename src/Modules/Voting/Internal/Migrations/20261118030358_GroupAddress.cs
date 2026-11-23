using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LunchSuggest.Modules.Voting.Internal.Migrations
{
    /// <inheritdoc />
    public partial class GroupAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Address",
                schema: "voting",
                table: "Sessions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Address",
                schema: "voting",
                table: "Sessions");
        }
    }
}
