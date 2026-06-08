using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog3d.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddModelVisibilityAndShares : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "visibility",
                table: "models",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Private");

            migrationBuilder.CreateTable(
                name: "model_shares",
                columns: table => new
                {
                    model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_shares", x => new { x.model_id, x.principal });
                    table.ForeignKey(
                        name: "FK_model_shares_models_model_id",
                        column: x => x.model_id,
                        principalTable: "models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_model_shares_principal",
                table: "model_shares",
                column: "principal");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_shares");

            migrationBuilder.DropColumn(
                name: "visibility",
                table: "models");
        }
    }
}
