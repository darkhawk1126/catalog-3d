namespace Catalog3d.Domain.Enums;

/// <summary>
/// Per-collection roles. Higher numeric value implies a strictly-superset permission set,
/// enabling simple >= comparisons in policy evaluation.
/// </summary>
public enum CollectionRole
{
    Preview = 1,
    Download = 2,
    Uploader = 3,
    Admin = 4
}
