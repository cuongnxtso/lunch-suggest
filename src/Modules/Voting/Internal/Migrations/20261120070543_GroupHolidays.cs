using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LunchSuggest.Modules.Voting.Internal.Migrations
{
    /// <inheritdoc />
    public partial class GroupHolidays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<DateOnly>>(
                name: "Holidays",
                schema: "voting",
                table: "Sessions",
                type: "date[]",
                nullable: false,
                defaultValueSql: "'{}'"); // the existing group: no holidays yet
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Holidays",
                schema: "voting",
                table: "Sessions");
        }
    }
}
