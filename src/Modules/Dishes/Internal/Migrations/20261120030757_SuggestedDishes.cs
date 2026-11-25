using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LunchSuggest.Modules.Dishes.Internal.Migrations
{
    /// <inheritdoc />
    public partial class SuggestedDishes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SuggestedDishes",
                schema: "dishes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlaceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PlaceName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Dish = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SuggestedBy = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SuggestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Approved = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SuggestedDishes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SuggestedDishes_PlaceId",
                schema: "dishes",
                table: "SuggestedDishes",
                column: "PlaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SuggestedDishes",
                schema: "dishes");
        }
    }
}
