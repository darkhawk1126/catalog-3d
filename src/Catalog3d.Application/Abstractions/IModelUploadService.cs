using Catalog3d.Domain.Entities;

namespace Catalog3d.Application.Abstractions;

/// <summary>
/// Owns the corrected upload pipeline shared by both the REST endpoint and the Blazor UI.
/// Streams the file to IFileStore, validates the binary-STL signature, enforces the
/// configured size ceiling, derives the slug, persists Model + ModelFile, and enqueues
/// the render job. Returns a discriminated result so callers map to HTTP / UI without
/// catching exceptions for control flow.
/// </summary>
public interface IModelUploadService
{
    /// <summary>
    /// Execute the upload pipeline.
    /// </summary>
    /// <param name="request">Validated input from the caller (multipart fields + stream).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ModelUploadResult> UploadAsync(ModelUploadRequest request, CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// Request
// ---------------------------------------------------------------------------

/// <summary>
/// All caller-supplied inputs for an upload. The <see cref="FileStream"/> is consumed
/// exactly once and is NOT disposed by the service — the caller owns the stream lifetime.
/// </summary>
public sealed class ModelUploadRequest
{
    public required Guid CollectionId { get; init; }

    /// <summary>Uploader's user ID, stored in Model.Owner.</summary>
    public required string OwnerId { get; init; }

    /// <summary>
    /// Raw file byte stream. The service reads it exactly once without seeking.
    /// </summary>
    public required Stream FileStream { get; init; }

    /// <summary>
    /// Original filename from the multipart disposition, used for slug derivation
    /// when <see cref="SlugOverride"/> is null.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Optional caller-supplied slug. When non-null and non-empty this overrides
    /// the filename-derived slug.
    /// </summary>
    public string? SlugOverride { get; init; }

    /// <summary>Human-readable model name; falls back to the derived slug when null.</summary>
    public string? ModelName { get; init; }

    /// <summary>Optional model description.</summary>
    public string? Description { get; init; }
}

// ---------------------------------------------------------------------------
// Discriminated result
// ---------------------------------------------------------------------------

/// <summary>
/// Discriminated union returned by <see cref="IModelUploadService.UploadAsync"/>.
/// </summary>
public abstract record ModelUploadResult
{
    private ModelUploadResult() { }

    /// <summary>Upload succeeded. Both entities are persisted and the render job is queued.</summary>
    public sealed record Success(Model Model, ModelFile File) : ModelUploadResult;

    /// <summary>
    /// The derived or supplied slug already exists in the catalog.
    /// The orphaned blob has been deleted before returning this result.
    /// </summary>
    public sealed record SlugConflict(string Slug) : ModelUploadResult;

    /// <summary>The derived slug is empty (e.g. filename was all punctuation).</summary>
    public sealed record InvalidSlug(string Reason) : ModelUploadResult;

    /// <summary>
    /// The file content failed binary-STL validation.
    /// Reason describes what was wrong (e.g. "header too short", "triangle count mismatch").
    /// </summary>
    public sealed record InvalidContent(string Reason) : ModelUploadResult;

    /// <summary>
    /// The upload was aborted because the byte count exceeded the configured ceiling
    /// (<c>Upload:MaxSizeBytes</c>). The partial blob has been deleted.
    /// </summary>
    public sealed record TooLarge(long LimitBytes) : ModelUploadResult;
}
