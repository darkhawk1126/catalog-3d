using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Catalog3d.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Catalog3d.Infrastructure.Rendering;

/// <summary>
/// Calls the render sidecar's /render endpoint to produce a thumbnail PNG.
///
/// HTTP contract (app → sidecar):
///   POST {RenderSidecar:BaseUrl}/render
///   Content-Type: application/json
///   Body: { "blobKey": "&lt;sha256-hex&gt;" }
///
///   On success (HTTP 200):
///   Body: { "thumbnailKey": "&lt;sha256-hex&gt;" }
///   The sidecar has already written the PNG to the shared volume at:
///     {volumeRoot}/thumbs/{key[0..2]}/{key}.png
///   The thumbnailKey equals the blobKey (1:1 STL → PNG mapping; keyed by STL hash).
///
///   On failure (HTTP 4xx / 5xx):
///   Body: { "error": "&lt;human-readable message&gt;" }
///   OR the connection may fail (timeout, refused). Both produce RenderResult.Failure.
///
/// The app never streams STL bytes to the sidecar. Both containers share the blob PVC;
/// passing the key is sufficient and eliminates a double-copy over loopback.
/// </summary>
internal sealed class SidecarThumbnailRenderer : IThumbnailRenderer
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SidecarThumbnailRenderer> _logger;

    public SidecarThumbnailRenderer(
        HttpClient httpClient,
        ILogger<SidecarThumbnailRenderer> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<RenderResult> RenderAsync(
        string blobKey,
        CancellationToken cancellationToken = default)
    {
        var request = new RenderRequest(blobKey);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .PostAsJsonAsync("/render", request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Render sidecar unreachable for blob {BlobKey}.", blobKey);
            return RenderResult.Failure($"Sidecar request failed: {ex.Message}");
        }

        if (response.IsSuccessStatusCode)
        {
            var ok = await response.Content
                .ReadFromJsonAsync<RenderSuccessResponse>(cancellationToken)
                .ConfigureAwait(false);

            if (ok is null)
                return RenderResult.Failure("Sidecar returned 200 but body was null.");

            _logger.LogInformation(
                "Thumbnail rendered for blob {BlobKey}; thumbnail key {ThumbnailKey}.",
                blobKey, ok.ThumbnailKey);

            return RenderResult.Ok();
        }

        string errorBody;
        try
        {
            var err = await response.Content
                .ReadFromJsonAsync<RenderErrorResponse>(cancellationToken)
                .ConfigureAwait(false);
            errorBody = err?.Error ?? response.ReasonPhrase ?? "unknown";
        }
        catch
        {
            errorBody = $"HTTP {(int)response.StatusCode}";
        }

        _logger.LogWarning(
            "Render sidecar returned {Status} for blob {BlobKey}: {Error}.",
            (int)response.StatusCode, blobKey, errorBody);

        return RenderResult.Failure(errorBody);
    }
}

// ─── Wire shapes ────────────────────────────────────────────────────────────

/// <summary>POST /render request body.</summary>
internal sealed record RenderRequest(
    [property: JsonPropertyName("blobKey")] string BlobKey);

/// <summary>POST /render success response body (HTTP 200).</summary>
internal sealed record RenderSuccessResponse(
    [property: JsonPropertyName("thumbnailKey")] string ThumbnailKey);

/// <summary>POST /render error response body (HTTP 4xx / 5xx).</summary>
internal sealed record RenderErrorResponse(
    [property: JsonPropertyName("error")] string Error);
