namespace Catalog3d.Infrastructure.Upload;

public sealed class ModelUploadOptions
{
    public const string SectionName = "Upload";

    /// <summary>
    /// Maximum allowed upload size in bytes. Requests exceeding this are aborted and
    /// the caller receives <see cref="Catalog3d.Application.Abstractions.ModelUploadResult.TooLarge"/>.
    /// Default: 100 MiB.
    /// </summary>
    public long MaxSizeBytes { get; set; } = 100 * 1024 * 1024;
}
