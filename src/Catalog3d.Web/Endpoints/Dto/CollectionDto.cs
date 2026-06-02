namespace Catalog3d.Web.Endpoints.Dto;

/// <summary>
/// Wire representation of a Collection returned by the v1 API.
/// Field set is intentionally minimal for m1; expand in later milestones.
/// </summary>
public sealed record CollectionDto(
    Guid Id,
    string Slug,
    string Name,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
