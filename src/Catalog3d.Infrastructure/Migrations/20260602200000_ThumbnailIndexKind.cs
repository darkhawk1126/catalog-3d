using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog3d.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ThumbnailIndexKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The original (model_id, blob_key) unique index prevents inserting a Thumbnail row
            // that shares the same blob key as its parent STL row — which is required by design
            // because the sidecar keys the PNG by the STL content hash. Replace it with a
            // (model_id, blob_key, kind) unique index so each (model, blob, role) pair is unique.
            migrationBuilder.DropIndex(
                name: "ix_model_files_model_blobkey",
                table: "model_files");

            migrationBuilder.CreateIndex(
                name: "ix_model_files_model_blobkey_kind",
                table: "model_files",
                columns: new[] { "model_id", "blob_key", "kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_model_files_model_blobkey_kind",
                table: "model_files");

            migrationBuilder.CreateIndex(
                name: "ix_model_files_model_blobkey",
                table: "model_files",
                columns: new[] { "model_id", "blob_key" },
                unique: true);
        }
    }
}
