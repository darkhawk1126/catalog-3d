using Catalog3d.RenderSidecar;

var builder = WebApplication.CreateBuilder(args);

// Bind sidecar-specific options (VolumeRoot, renderer binary path, etc.)
builder.Services.Configure<RenderSidecarHostOptions>(
    builder.Configuration.GetSection(RenderSidecarHostOptions.SectionName));

var app = builder.Build();

// Health probe — used by k8s liveness check and docker-compose healthcheck.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

// POST /render — invoked by the app's SidecarThumbnailRenderer.
// Request body:  { "blobKey": "<sha256-hex>" }
// Success (200): { "thumbnailKey": "<sha256-hex>" }
// Failure (422): { "error": "<message>" }
//
// The sidecar resolves paths on the shared volume using RenderSidecarHostOptions.VolumeRoot:
//   read  : {VolumeRoot}/blobs/{blobKey[0..2]}/{blobKey}
//   write : {VolumeRoot}/thumbs/{blobKey[0..2]}/{blobKey}.png
//
// Downstream agent (render-sidecar-impl) owns the implementation body.
// The stub returns 501 so the CI build passes without the renderer binary present.
app.MapPost("/render", RenderEndpoints.HandleRenderAsync);

app.Run();
