using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LunchSuggest.Modules.Voting.Internal.Migrations
{
    /// <inheritdoc />
    public partial class CandidateDish : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Candidates_SessionId_PlaceId",
                schema: "voting",
                table: "Candidates");

            migrationBuilder.AddColumn<string>(
                name: "Dish",
                schema: "voting",
                table: "Candidates",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Candidates_SessionId_PlaceId_Dish",
                schema: "voting",
                table: "Candidates",
                columns: new[] { "SessionId", "PlaceId", "Dish" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Candidates_SessionId_PlaceId_Dish",
                schema: "voting",
                table: "Candidates");

            migrationBuilder.DropColumn(
                name: "Dish",
                schema: "voting",
                table: "Candidates");

            migrationBuilder.CreateIndex(
                name: "IX_Candidates_SessionId_PlaceId",
                schema: "voting",
                table: "Candidates",
                columns: new[] { "SessionId", "PlaceId" },
                unique: true);
        }
    }
}
