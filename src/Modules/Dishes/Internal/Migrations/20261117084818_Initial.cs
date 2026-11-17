using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LunchSuggest.Modules.Dishes.Internal.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "dishes");

            migrationBuilder.CreateTable(
                name: "PlaceDishes",
                schema: "dishes",
                columns: table => new
                {
                    PlaceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Dishes = table.Column<List<string>>(type: "text[]", nullable: false),
                    LookedUpAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaceDishes", x => x.PlaceId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaceDishes",
                schema: "dishes");
        }
    }
}
