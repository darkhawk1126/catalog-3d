namespace Catalog3d.Infrastructure.Rendering;

/// <summary>
/// Configuration for the stl-thumb render sidecar HTTP client and shared-volume path.
///
/// Configuration section: "RenderSidecar"
///
/// Keys:
///   RenderSidecar:BaseUrl        — HTTP base URL of the render sidecar container.
///                                  Dev default: http://localhost:5200
///                                  Prod (k8s sidecar): http://localhost:5200 (same pod, loopback)
///   RenderSidecar:TimeoutSeconds — Per-render HTTP timeout. Default: 60.
///   RenderSidecar:VolumeRoot     — Absolute path to the shared blob volume root.
///                                  Must match Storage:Root — both sides of the sidecar boundary
///                                  use the same physical path (bind-mount dev / PVC prod).
///                                  The sidecar uses this to resolve:
///                                    read  : {VolumeRoot}/blobs/{key[0..2]}/{key}
///                                    write : {VolumeRoot}/thumbs/{key[0..2]}/{key}.png
///                                  Default: same value as Storage:Root (sidecar reads its own env).
/// </summary>
public sealed class RenderSidecarOptions
{
    public const string SectionName = "RenderSidecar";

    /// <summary>HTTP base URL of the render sidecar. Required.</summary>
    public string BaseUrl { get; set; } = "http://localhost:5200";

    /// <summary>Per-render HTTP timeout in seconds. Default 60.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Absolute filesystem root shared between the app and the sidecar.
    /// The app reads this to serve thumbnails via IFileStore.
    /// The sidecar reads its own copy of this setting from its environment.
    /// They must resolve to the same physical path on the shared volume.
    /// </summary>
    public string VolumeRoot { get; set; } = string.Empty;
}
