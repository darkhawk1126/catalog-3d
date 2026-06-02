namespace Catalog3d.Domain.Entities;

public sealed class ModelFile
{
    public Guid Id { get; init; }
    public Guid ModelId { get; set; }
    public ModelFileKind Kind { get; set; }

    /// <summary>Content-addressed storage key (SHA-256 hex).</summary>
    public string BlobKey { get; set; } = string.Empty;

    public long Size { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public int? TriCount { get; set; }

    /// <summary>Axis-aligned bounding box stored as serialized JSON, e.g. {min:[x,y,z],max:[x,y,z]}.</summary>
    public string? BoundingBox { get; set; }

    public RenderStatus RenderStatus { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Model Model { get; set; } = null!;
}

public enum ModelFileKind
{
    Stl,
    Thumbnail,
    Other
}

public enum RenderStatus
{
    Pending,
    Processing,
    Complete,
    Failed
}
