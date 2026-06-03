using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog3d.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class GlobalSlugUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_models_collection_id",
                table: "models",
                column: "collection_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_models_collection_id",
                table: "models");
        }
    }
}
