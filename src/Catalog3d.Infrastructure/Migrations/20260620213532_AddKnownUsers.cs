using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog3d.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKnownUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "known_users",
                columns: table => new
                {
                    principal = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    username = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    display_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    groups_csv = table.Column<string>(type: "text", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_known_users", x => x.principal);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "known_users");
        }
    }
}
