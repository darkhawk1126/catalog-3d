using Catalog3d.Domain.Enums;

namespace Catalog3d.Domain.Entities;

public sealed class Model
{
    public Guid Id { get; init; }
    public Guid CollectionId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public ModelStatus Status { get; set; }

    /// <summary>Per-model sharing state. Defaults to Private (owner only).</summary>
    public ModelVisibility Visibility { get; set; } = ModelVisibility.Private;

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Collection Collection { get; set; } = null!;
    public ICollection<ModelFile> Files { get; init; } = new List<ModelFile>();

    /// <summary>Explicit per-principal grants used when Visibility is Shared.</summary>
    public ICollection<ModelShare> Shares { get; init; } = new List<ModelShare>();
}

public enum ModelStatus
{
    Processing,
    Ready,
    Failed
}
