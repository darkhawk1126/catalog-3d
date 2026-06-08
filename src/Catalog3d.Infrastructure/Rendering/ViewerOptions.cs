namespace Catalog3d.Infrastructure.Rendering;

/// <summary>
/// Configuration for the embeddable three.js viewer.
///
/// Configuration section: "Viewer"
///
/// Keys:
///   Viewer:GeometryUrlPattern — URL template the viewer JS uses to fetch the STL geometry.
///                               The placeholder {fileId} is replaced by the ModelFile GUID.
///                               Default: /api/v1/models/files/{fileId}
///                               Must match the route registered in EndpointRegistration.
///   Viewer:MaxFileSizeBytes   — Soft UX limit: the viewer warns (but does not block) when
///                               the STL exceeds this size. Not a security boundary.
///                               Default: 52428800 (50 MiB).
/// </summary>
public sealed class ViewerOptions
{
    public const string SectionName = "Viewer";

    /// <summary>
    /// URL template for the geometry fetch. The viewer JS replaces "{fileId}" with the
    /// ModelFile GUID string. Must resolve to the Download-gated geometry endpoint:
    ///   GET /api/v1/models/files/{fileId}
    /// </summary>
    public string GeometryUrlPattern { get; set; } = "/api/v1/models/files/{fileId}";

    /// <summary>Soft UX file-size warning threshold in bytes. Default 50 MiB.</summary>
    public long MaxFileSizeBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>
    /// The CSP <c>frame-ancestors</c> source list for the /viewer and /viewer/embed routes —
    /// i.e. which origins may iframe the viewer. Space-separated, CSP syntax. Defaults to the
    /// homelab wiki hosts; override per environment (e.g. to add a dev wiki origin).
    ///
    /// The deployed homelab wiki is mediawiki.mallcop.dev; wiki.mallcop.dev is kept as an
    /// accepted alias. All other routes always send <c>frame-ancestors 'none'</c>.
    /// </summary>
    public string FrameAncestors { get; set; } =
        "'self' https://mediawiki.mallcop.dev https://wiki.mallcop.dev";
}
