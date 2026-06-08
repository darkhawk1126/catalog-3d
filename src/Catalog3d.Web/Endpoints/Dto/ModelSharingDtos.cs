namespace Catalog3d.Web.Endpoints.Dto;

/// <summary>Request body for PUT /api/v1/models/{slug}/visibility.</summary>
/// <param name="Visibility">One of "Private", "Shared", "Public" (case-insensitive).</param>
public sealed record SetVisibilityRequest(string Visibility);

/// <summary>Request body for POST /api/v1/models/{slug}/shares.</summary>
/// <param name="Principal">Principal to grant Download, e.g. "user:&lt;sub&gt;" or "group:&lt;name&gt;"
/// (OIDC) or a bare dev username.</param>
public sealed record AddShareRequest(string Principal);

/// <summary>One entry in the share list returned by GET /api/v1/models/{slug}/shares.</summary>
public sealed record ModelShareDto(string Principal, DateTimeOffset CreatedAt);
