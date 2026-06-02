using Catalog3d.Domain.Enums;

namespace Catalog3d.Domain.Entities;

public sealed class RoleAssignment
{
    public Guid CollectionId { get; set; }
    public string Principal { get; set; } = string.Empty;
    public CollectionRole Role { get; set; }

    public Collection Collection { get; set; } = null!;
}
