namespace Catalog3d.Web.Endpoints.Dto;

/// <summary>Response returned after a successful streaming model upload.</summary>
public sealed record UploadModelResponse(
    Guid ModelId,
    Guid FileId,
    string BlobKey,
    long Size,
    string Sha256);
