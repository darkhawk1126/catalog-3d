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
            // Replace the per-(collection_id, slug) unique index with a globally unique index
            // on slug alone. Model slugs are the stable public identifiers used in wiki links
            // (/models/{slug}), so they must be unique across the entire catalog, not just per
            // collection. This also eliminates the sequential-scan risk for slug-only lookups.
            migrationBuilder.DropIndex(
                name: "ix_models_collection_slug",
                table: "models");

            migrationBuilder.CreateIndex(
                name: "ix_models_slug",
                table: "models",
                column: "slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_models_slug",
                table: "models");

            migrationBuilder.CreateIndex(
                name: "ix_models_collection_slug",
                table: "models",
                columns: new[] { "collection_id", "slug" },
                unique: true);
        }
    }
}
