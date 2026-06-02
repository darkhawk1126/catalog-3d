using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Catalog3d.RenderSidecar;

/// <summary>
/// Endpoint handlers for the render sidecar.
/// </summary>
internal static class RenderEndpoints
{
    // Process timeout slightly shorter than the app's HTTP client timeout (60 s).
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(55);

    // Minimum PNG file size; any file smaller indicates a corrupt or empty render.
    private const long MinPngBytes = 1024;

    internal static async Task<IResult> HandleRenderAsync(
        RenderRequest request,
        IOptions<RenderSidecarHostOptions> options,
        ILogger<RenderRequest> logger,
        CancellationToken cancellationToken)
    {
        var blobKey = request.BlobKey?.Trim();
        if (string.IsNullOrEmpty(blobKey))
        {
            return Results.BadRequest(new RenderErrorResponse("blobKey is required."));
        }

        // SHA-256 hex is 64 characters; validate loosely (hex chars, correct length).
        if (blobKey.Length != 64 || !IsHexString(blobKey))
        {
            return Results.BadRequest(new RenderErrorResponse("blobKey must be a 64-character hex SHA-256 string."));
        }

        var opts = options.Value;
        var shard = blobKey[..2];

        var stlPath = Path.Combine(opts.VolumeRoot, "blobs", shard, blobKey);
        var thumbDir = Path.Combine(opts.VolumeRoot, "thumbs", shard);
        var pngPath = Path.Combine(thumbDir, blobKey + ".png");

        if (!File.Exists(stlPath))
        {
            logger.LogWarning("Blob not found at {StlPath} for key {BlobKey}.", stlPath, blobKey);
            return Results.UnprocessableEntity(new RenderErrorResponse("blob not found"));
        }

        // Ensure output directory exists before invoking the renderer.
        Directory.CreateDirectory(thumbDir);

        logger.LogInformation(
            "Rendering thumbnail for {BlobKey}: {StlPath} → {PngPath} via {Binary}",
            blobKey, stlPath, pngPath, opts.RendererBinary);

        var (exitCode, stdout, stderr) = await RunRendererAsync(
            opts.RendererBinary,
            opts.RenderWidth,
            opts.RenderHeight,
            stlPath,
            pngPath,
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            logger.LogError(
                "Renderer exited with code {ExitCode} for {BlobKey}. stdout={Stdout} stderr={Stderr}",
                exitCode, blobKey, stdout, stderr);
            return Results.UnprocessableEntity(
                new RenderErrorResponse($"renderer exited {exitCode}: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}"));
        }

        var fi = new FileInfo(pngPath);
        if (!fi.Exists || fi.Length < MinPngBytes)
        {
            logger.LogError(
                "Renderer succeeded but PNG is missing or too small ({Bytes} bytes) for {BlobKey}.",
                fi.Exists ? fi.Length : 0, blobKey);
            return Results.UnprocessableEntity(
                new RenderErrorResponse("renderer produced no output or an empty PNG"));
        }

        logger.LogInformation(
            "Thumbnail ready for {BlobKey}: {PngPath} ({Bytes} bytes).",
            blobKey, pngPath, fi.Length);

        return Results.Ok(new RenderSuccessResponse(blobKey));
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunRendererAsync(
        string binary,
        int width,
        int height,
        string stlPath,
        string pngPath,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // stl-thumb renders square output; use the larger dimension so the thumbnail
        // covers the configured area regardless of whether width != height in config.
        var size = Math.Max(width, height);

        // stl-thumb CLI: stl-thumb [-s <size>] <STL_FILE> <IMG_FILE>
        // The binary auto-detects OSMesa when Wayland/X11/EGL are unavailable.
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(size.ToString());
        psi.ArgumentList.Add(stlPath);
        psi.ArgumentList.Add(pngPath);

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProcessTimeout);

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            return (process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Process timeout — kill and report.
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return (-1, string.Empty, $"renderer timed out after {ProcessTimeout.TotalSeconds:F0} s");
        }
    }

    private static bool IsHexString(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        return true;
    }
}

/// <summary>POST /render request body.</summary>
internal sealed record RenderRequest(string BlobKey);

/// <summary>POST /render success response (HTTP 200).</summary>
internal sealed record RenderSuccessResponse(
    [property: JsonPropertyName("thumbnailKey")] string ThumbnailKey);

/// <summary>POST /render error response (HTTP 4xx/5xx).</summary>
internal sealed record RenderErrorResponse(
    [property: JsonPropertyName("error")] string Error);
