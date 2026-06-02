namespace Catalog3d.Infrastructure.Storage;

public sealed class DiskFileStoreOptions
{
    public const string SectionName = "Storage";

    /// <summary>Absolute filesystem root where blobs/ and thumbs/ subdirectories reside.</summary>
    public string Root { get; set; } = string.Empty;
}
