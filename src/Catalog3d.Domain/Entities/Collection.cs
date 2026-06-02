namespace Catalog3d.Domain.Entities;

public sealed class Collection
{
    public Guid Id { get; init; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<Model> Models { get; init; } = new List<Model>();
    public ICollection<RoleAssignment> RoleAssignments { get; init; } = new List<RoleAssignment>();
}
