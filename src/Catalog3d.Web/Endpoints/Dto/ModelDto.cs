using Catalog3d.Domain.Entities;

namespace Catalog3d.Web.Endpoints.Dto;

/// <summary>
/// Wire representation of a Model returned by the v1 API.
/// </summary>
public sealed record ModelDto(
    Guid Id,
    Guid CollectionId,
    string Slug,
    string Name,
    string Description,
    string Owner,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static ModelDto FromEntity(Model m) => new(
        m.Id,
        m.CollectionId,
        m.Slug,
        m.Name,
        m.Description,
        m.Owner,
        m.Status.ToString(),
        m.CreatedAt,
        m.UpdatedAt);
}
