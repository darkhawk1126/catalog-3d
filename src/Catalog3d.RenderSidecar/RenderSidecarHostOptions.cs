namespace Catalog3d.RenderSidecar;

/// <summary>
/// Configuration for the render sidecar host process.
///
/// Configuration section: "Sidecar"
///
/// Keys:
///   Sidecar:VolumeRoot         — Absolute path to the shared blob volume mounted into this container.
///                                Injected via environment variable SIDECAR__VOLUMEROOT in k8s/compose.
///                                Must match the app container's Storage:Root.
///                                Layout:
///                                  {VolumeRoot}/blobs/{key[0..2]}/{key}          — STL source
///                                  {VolumeRoot}/thumbs/{key[0..2]}/{key}.png     — PNG output
///   Sidecar:RendererBinary     — Absolute path to the stl-thumb binary.
///                                Default: /usr/bin/stl-thumb
///   Sidecar:RenderWidth        — Output PNG width in pixels. Default: 512.
///   Sidecar:RenderHeight       — Output PNG height in pixels. Default: 512.
///                                Note: stl-thumb renders square images via a single -s flag.
///                                When RenderWidth != RenderHeight the larger value is used so
///                                the thumbnail covers the full configured area.
/// </summary>
public sealed class RenderSidecarHostOptions
{
    public const string SectionName = "Sidecar";

    /// <summary>Shared volume root. Sidecar reads STLs and writes PNGs here.</summary>
    public string VolumeRoot { get; set; } = string.Empty;

    /// <summary>Absolute path to the stl-thumb binary.</summary>
    public string RendererBinary { get; set; } = "/usr/bin/stl-thumb";

    /// <summary>Render output width in pixels.</summary>
    public int RenderWidth { get; set; } = 512;

    /// <summary>
    /// Render output height in pixels.
    /// stl-thumb renders square images; the larger of RenderWidth/RenderHeight is used.
    /// </summary>
    public int RenderHeight { get; set; } = 512;
}
