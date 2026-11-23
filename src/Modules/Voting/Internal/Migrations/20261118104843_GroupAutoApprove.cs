using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LunchSuggest.Modules.Voting.Internal.Migrations
{
    /// <inheritdoc />
    public partial class GroupAutoApprove : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoApprove",
                schema: "voting",
                table: "Sessions",
                type: "boolean",
                nullable: false,
                defaultValue: true); // the existing group keeps the list active
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoApprove",
                schema: "voting",
                table: "Sessions");
        }
    }
}
