using Catalog3d.Application.Abstractions;
using Catalog3d.Domain.Entities;
using Catalog3d.Domain.Enums;
using Catalog3d.Infrastructure.Persistence;
using Catalog3d.Infrastructure.Rendering;
using Catalog3d.Web.Auth;
using Catalog3d.Web.Endpoints.Dto;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Catalog3d.Web.Endpoints;

internal static class EndpointRegistration
{
    /// <summary>
    /// Maps all catalog API endpoints and the viewer route.
    /// Endpoint handler files are added by the endpoint-implementation agent;
    /// this method body must NOT be edited by Program.cs or project scaffolding agents.
    /// </summary>
    internal static WebApplication MapCatalogEndpoints(this WebApplication app)
    {
        // Dev-mode sign-in/sign-out form endpoints. Active only when Auth:Provider = Dev.
        var provider = app.Configuration["Auth:Provider"] ?? "Dev";
        if (!provider.Equals("Oidc", StringComparison.OrdinalIgnoreCase))
            app.MapDevLoginEndpoints();

        // .WithOpenApi() is deprecated in .NET 10; OpenAPI document generation is automatic.
        var v1 = app.MapGroup("/api/v1");

        // Collections
        var collections = v1.MapGroup("/collections");

        // GET /api/v1/collections
        collections.MapGet("/", GetCollectionsAsync)
            .RequireAuthorization();

        // GET /api/v1/collections/{slug}
        collections.MapGet("/{slug}", GetCollectionBySlugAsync)
            .RequireAuthorization();

        // GET /api/v1/collections/{slug}/models
        collections.MapGet("/{slug}/models", GetCollectionModelsAsync)
            .RequireAuthorization();

        // POST /api/v1/collections/{slug}/models  (streaming multipart upload)
        // DisableAntiforgery is correct here: this is a REST endpoint for programmatic
        // clients (API, MediaWiki plugin), not a browser form. CSRF is instead blocked
        // by the Origin check inside the handler, which rejects cross-site browser
        // requests while allowing token-authenticated API clients (no Origin header).
        collections.MapPost("/{slug}/models", UploadModelAsync)
            .RequireAuthorization()
            .DisableAntiforgery();

        // Models
        var models = v1.MapGroup("/models");

        // GET /api/v1/models/{slug}
        models.MapGet("/{slug}", GetModelBySlugAsync)
            .RequireAuthorization();

        // GET /api/v1/models/{slug}/thumbnail — Preview-gated PNG
        // Serves the precomputed thumbnail for the named model.
        // Authorization: caller must hold CollectionRole.Preview (or higher) on the model's collection.
        // Response: 200 image/png on hit; 404 when model unknown, caller lacks Preview, or thumbnail
        //           not yet rendered (render still pending or failed).
        models.MapGet("/{slug}/thumbnail", GetModelThumbnailAsync)
            .RequireAuthorization();

        // GET /api/v1/models/files/{fileId} — Download-gated STL stream
        // Streams the raw STL bytes for a ModelFile identified by its GUID.
        // Authorization: caller must hold CollectionRole.Download (or higher) on the file's collection.
        // This is the geometry URL that the viewer JS fetches; it MUST remain stable.
        // The viewer constructs this URL as: GeometryUrlPattern with "{fileId}" replaced by the GUID string.
        // Response: 200 application/octet-stream (or model/stl) with Content-Disposition: attachment.
        models.MapGet("/files/{fileId:guid}", GetModelFileAsync)
            .RequireAuthorization();

        // Viewer
        // GET /viewer/{fileId} — Download-gated embeddable HTML viewer
        // Returns a self-contained HTML page that bootstraps the three.js STL viewer.
        // Authorization: caller must hold CollectionRole.Download — interactive viewing ≡ geometry access.
        // The viewer JS fetches geometry via: GET /api/v1/models/files/{fileId}
        // Route is intentionally outside /api/v1 so it is directly iframe-able from the wiki.
        app.MapGet("/viewer/{fileId:guid}", GetViewerAsync)
            .RequireAuthorization();

        return app;
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/collections
    // Returns all collections where the caller holds at least Preview.
    // -------------------------------------------------------------------------
    private static async Task<IResult> GetCollectionsAsync(
        [FromServices] ICollectionAuthorizationService authService,
        [FromServices] IUserContext userContext,
        [FromServices] CatalogDbContext db,
        CancellationToken cancellationToken)
    {
        var authorizedIds = await authService
            .GetAuthorizedCollectionIdsAsync(userContext, CollectionRole.Preview, cancellationToken)
            .ConfigureAwait(false);

        if (authorizedIds.Count == 0)
            return Results.Ok(Array.Empty<CollectionDto>());

        var dtos = await db.Collections
            .Where(c => authorizedIds.Contains(c.Id))
            .OrderBy(c => c.Name)
            .Select(c => new CollectionDto(c.Id, c.Slug, c.Name, c.Description, c.CreatedAt, c.UpdatedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(dtos);
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/collections/{slug}
    // Returns collection detail; 404 when the caller has no role (not visible).
    // -------------------------------------------------------------------------
    private static async Task<IResult> GetCollectionBySlugAsync(
        string slug,
        [FromServices] ICollectionAuthorizationService authService,
        [FromServices] IUserContext userContext,
        [FromServices] CatalogDbContext db,
        CancellationToken cancellationToken)
    {
        var collection = await db.Collections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Slug == slug, cancellationToken)
            .ConfigureAwait(false);

        // Return 404 rather than 403 — collections are invisible to unauthorized callers.
        if (collection is null)
            return Results.NotFound();

        var hasAccess = await authService
            .AuthorizeAsync(collection.Id, userContext, CollectionRole.Preview, cancellationToken)
            .ConfigureAwait(false);

        if (!hasAccess)
            return Results.NotFound();

        var dto = new CollectionDto(
            collection.Id, collection.Slug, collection.Name,
            collection.Description, collection.CreatedAt, collection.UpdatedAt);

        return Results.Ok(dto);
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/collections/{slug}/models
    // Lists models in the collection; Preview-gated.
    // -------------------------------------------------------------------------
    private static async Task<IResult> GetCollectionModelsAsync(
        string slug,
        [FromServices] ICollectionAuthorizationService authService,
        [FromServices] IUserContext userContext,
        [FromServices] CatalogDbContext db,
        CancellationToken cancellationToken)
    {
        var collection = await db.Collections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Slug == slug, cancellationToken)
            .ConfigureAwait(false);

        if (collection is null)
            return Results.NotFound();

        var hasAccess = await authService
            .AuthorizeAsync(collection.Id, userContext, CollectionRole.Preview, cancellationToken)
            .ConfigureAwait(false);

        if (!hasAccess)
            return Results.NotFound();

        // Fetch entities then map client-side; enum.ToString() cannot be translated to SQL.
        var models = await db.Models
            .AsNoTracking()
            .Where(m => m.CollectionId == collection.Id)
            .OrderBy(m => m.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var dtos = models.ConvertAll(ModelDto.FromEntity);

        return Results.Ok(dtos);
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/models/{slug}
    // Returns model metadata; Preview-gated via the parent collection.
    // -------------------------------------------------------------------------
    private static async Task<IResult> GetModelBySlugAsync(
        string slug,
        [FromServices] ICollectionAuthorizationService authService,
        [FromServices] IUserContext userContext,
        [FromServices] CatalogDbContext db,
        CancellationToken cancellationToken)
    {
        var model = await db.Models
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Slug == slug, cancellationToken)
            .ConfigureAwait(false);

        if (model is null)
            return Results.NotFound();

        var hasAccess = await authService
            .AuthorizeAsync(model.CollectionId, userContext, CollectionRole.Preview, cancellationToken)
            .ConfigureAwait(false);

        if (!hasAccess)
            return Results.NotFound();

        return Results.Ok(ModelDto.FromEntity(model));
    }

    // -------------------------------------------------------------------------
    // POST /api/v1/collections/{slug}/models
    // Uploader-gated. Streams multipart body to IModelUploadService — never buffers.
    // Reads: "name", "description", "slug" form fields + "file" file part.
    // All upload logic (size enforcement, STL validation, slug dedup) lives in the
    // shared IModelUploadService so the Blazor UI and REST endpoint share one pipeline.
    // -------------------------------------------------------------------------
    private static async Task<IResult> UploadModelAsync(
        string slug,
        HttpContext httpContext,
        [FromServices] ICollectionAuthorizationService authService,
        [FromServices] IUserContext userContext,
        [FromServices] CatalogDbContext db,
        [FromServices] IModelUploadService uploadService,
        CancellationToken cancellationToken)
    {
        var collection = await db.Collections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Slug == slug, cancellationToken)
            .ConfigureAwait(false);

        if (collection is null)
            return Results.NotFound();

        // Single round-trip: fetch the effective role and branch locally.
        // Preview-only callers (who have a role but insufficient privilege) get 403.
        // No-role callers get 404 (collection stays invisible).
        var effectiveRole = await authService
            .GetEffectiveRoleAsync(collection.Id, userContext, cancellationToken)
            .ConfigureAwait(false);

        if (effectiveRole is null)
            return Results.NotFound();

        if (effectiveRole < CollectionRole.Uploader)
            return Results.Forbid();

        // CSRF guard: if the request carries an Origin header, it came from a browser.
        // Reject it unless the origin matches our own host. Pure programmatic clients
        // (curl, SDKs) send no Origin header and are always allowed through.
        var originHeader = httpContext.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(originHeader)
            && !originHeader.Contains(httpContext.Request.Host.Host, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Forbid();
        }

        if (!httpContext.Request.HasFormContentType)
            return Results.BadRequest("Request must be multipart/form-data.");

        var contentType = httpContext.Request.ContentType;
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest("Request must be multipart/form-data.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary))
            return Results.BadRequest("Missing multipart boundary.");

        // Parse the multipart body. Fields before the file part populate the request;
        // fields after the file part cannot be read (body is consumed once, no seeking).
        // Callers must send name/description/slug fields BEFORE the file part.
        var reader = new MultipartReader(boundary, httpContext.Request.Body);

        string? modelName = null;
        string? modelDescription = null;
        string? modelSlugOverride = null;
        IResult? uploadResult = null;

        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                continue;

            if (disposition.IsFileDisposition())
            {
                var fileName = disposition.FileName.Value ?? "upload";
                var request = new ModelUploadRequest
                {
                    CollectionId = collection.Id,
                    OwnerId = userContext.UserId,
                    FileStream = section.Body,
                    FileName = fileName,
                    SlugOverride = modelSlugOverride,
                    ModelName = modelName,
                    Description = modelDescription,
                };

                var result = await uploadService
                    .UploadAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                uploadResult = result switch
                {
                    ModelUploadResult.Success s => Results.Created(
                        $"/api/v1/models/{s.Model.Slug}",
                        new UploadModelResponse(
                            s.Model.Id,
                            s.File.Id,
                            s.Model.Slug,
                            s.File.BlobKey,
                            s.File.Size,
                            s.File.Sha256)),

                    ModelUploadResult.SlugConflict c =>
                        Results.Conflict(new { error = "slug_conflict", slug = c.Slug }),

                    ModelUploadResult.InvalidSlug i =>
                        Results.BadRequest(new { error = "invalid_slug", reason = i.Reason }),

                    ModelUploadResult.InvalidContent ic =>
                        Results.BadRequest(new { error = "invalid_content", reason = ic.Reason }),

                    ModelUploadResult.TooLarge tl =>
                        Results.BadRequest(new { error = "too_large", limitBytes = tl.LimitBytes }),

                    _ => Results.StatusCode(500),
                };

                break; // File part terminates the loop.
            }
            else if (disposition.IsFormDisposition())
            {
                var fieldName = disposition.Name.Value;
                using var sr = new StreamReader(section.Body);
                var value = await sr.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                if (fieldName?.Equals("name", StringComparison.OrdinalIgnoreCase) == true)
                    modelName = value;
                else if (fieldName?.Equals("description", StringComparison.OrdinalIgnoreCase) == true)
                    modelDescription = value;
                else if (fieldName?.Equals("slug", StringComparison.OrdinalIgnoreCase) == true)
                    modelSlugOverride = value;
            }
        }

        return uploadResult ?? Results.BadRequest("No file part found in multipart body.");
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/models/{slug}/thumbnail
    // Preview-gated. Serves the precomputed PNG from the "thumbs" blob subdirectory.
    // -------------------------------------------------------------------------
    private static async Task<IResult> GetModelThumbnailAsync(
        string slug,
        HttpContext httpContext,
        [FromServices] ICollectionAuthorizationService authService,
        [FromServices] IUserContext userContext,
        [FromServices] CatalogDbContext db,
        [FromServices] IFileStore fileStore,
        CancellationToken cancellationToken)
    {
        var model = await db.Models
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Slug == slug, cancellationToken)
            .ConfigureAwait(false);

        if (model is null)
            return Results.NotFound();

        // Collections are invisible to callers without Preview — return 404, not 403.
        var hasAccess = await authService
            .AuthorizeAsync(model.CollectionId, userContext, CollectionRole.Preview, cancellationToken)
            .ConfigureAwait(false);

        if (!hasAccess)
            return Results.NotFound();

        // Thumbnail must be Complete; pending/failed renders are not-found to Preview callers.
        var thumb = await db.ModelFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(
                f => f.ModelId == model.Id
                  && f.Kind == ModelFileKind.Thumbnail
                  && f.RenderStatus == RenderStatus.Complete,
                cancellationToken)
            .ConfigureAwait(false);

        if (thumb is null)
            return Results.NotFound();

        // The sidecar writes {blobKey}.png into the thumbs subdirectory.
        // DiskFileStore uses the key verbatim — append ".png" to match the sidecar output path.
        Stream stream;
        try
        {
            stream = await fileStore
                .ReadAsync(thumb.BlobKey + ".png", "thumbs", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return Results.NotFound();
        }

        // 1-hour public cache; thumbnails are content-addressed (key = STL hash) so they
        // never change for a given slug once rendered. The value is immutable for the
        // lifetime of the model file, but we use max-age=3600 as a conservative floor
        // (wikis may need to invalidate when a model is replaced).
        httpContext.Response.Headers.CacheControl = "public, max-age=3600, immutable";

        return Results.Stream(stream, "image/png", enableRangeProcessing: false);
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/models/files/{fileId}
    // Download-gated. Streams raw STL bytes. This is the geometry URL for the viewer.
    // Geometry URL pattern: /api/v1/models/files/{fileId}
    //   where {fileId} is the ModelFile.Id (Guid, lowercase, no braces).
    // -------------------------------------------------------------------------
    private static async Task<IResult> GetModelFileAsync(
        Guid fileId,
        [FromServices] ICollectionAuthorizationService authService,
        [FromServices] IUserContext userContext,
        [FromServices] CatalogDbContext db,
        [FromServices] IFileStore fileStore,
        CancellationToken cancellationToken)
    {
        var modelFile = await db.ModelFiles
            .AsNoTracking()
            .Include(f => f.Model)
            .FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken)
            .ConfigureAwait(false);

        if (modelFile is null)
            return Results.NotFound();

        // viewer ≡ download: both require the Download role. Invisible = 404.
        var hasAccess = await authService
            .AuthorizeAsync(modelFile.Model.CollectionId, userContext, CollectionRole.Download, cancellationToken)
            .ConfigureAwait(false);

        if (!hasAccess)
            return Results.NotFound();

        Stream stream;
        try
        {
            stream = await fileStore
                .ReadAsync(modelFile.BlobKey, "blobs", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return Results.NotFound();
        }

        // Content-addressed: slug is human-readable; blobKey is the stable download name.
        var downloadName = $"{modelFile.Model.Slug}.stl";

        return Results.Stream(
            stream,
            contentType: "model/stl",
            fileDownloadName: downloadName,
            enableRangeProcessing: true);
    }

    // -------------------------------------------------------------------------
    // GET /viewer/{fileId}
    // Download-gated embeddable HTML page. Bootstraps the three.js STL viewer.
    // The viewer JS fetches geometry at: GET /api/v1/models/files/{fileId}
    // -------------------------------------------------------------------------
    private static async Task<IResult> GetViewerAsync(
        Guid fileId,
        [FromServices] ICollectionAuthorizationService authService,
        [FromServices] IUserContext userContext,
        [FromServices] CatalogDbContext db,
        [FromServices] IOptions<ViewerOptions> viewerOptions,
        [FromServices] IWebHostEnvironment env,
        CancellationToken cancellationToken)
    {
        var modelFile = await db.ModelFiles
            .AsNoTracking()
            .Include(f => f.Model)
            .FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken)
            .ConfigureAwait(false);

        if (modelFile is null)
            return Results.NotFound();

        // Interactive viewer = download equivalent (cornerstone). Same gate as geometry endpoint.
        var hasAccess = await authService
            .AuthorizeAsync(modelFile.Model.CollectionId, userContext, CollectionRole.Download, cancellationToken)
            .ConfigureAwait(false);

        if (!hasAccess)
            return Results.NotFound();

        var opts = viewerOptions.Value;
        var geometryUrl = opts.GeometryUrlPattern.Replace(
            "{fileId}", fileId.ToString("D"), StringComparison.Ordinal);

        var html = BuildViewerHtml(env, geometryUrl);

        // SAMEORIGIN allows wiki iframe embedding from the same origin.
        return Results.Content(html, "text/html; charset=utf-8");
    }

    // Cached composed HTML: template + inlined bundle. Built once on first request.
    // The bundle is ~480 KB minified (three.js r177 + STLLoader + viewer logic).
    // Inlining avoids requiring UseStaticFiles middleware or cross-origin auth complications.
    private static string? _viewerHtmlBase;
    private static readonly Lock _viewerHtmlLock = new();

    private static string GetViewerHtmlBase(IWebHostEnvironment env)
    {
        if (_viewerHtmlBase is not null)
            return _viewerHtmlBase;

        lock (_viewerHtmlLock)
        {
            if (_viewerHtmlBase is not null)
                return _viewerHtmlBase;

            var viewerRoot = Path.Combine(env.WebRootPath, "viewer");
            var template = File.ReadAllText(Path.Combine(viewerRoot, "viewer-template.html"));
            var bundle = File.ReadAllText(Path.Combine(viewerRoot, "viewer-app.bundle.js"));
            _viewerHtmlBase = template.Replace("{{BUNDLE}}", bundle, StringComparison.Ordinal);
            return _viewerHtmlBase;
        }
    }

    // Builds a self-contained HTML page with the three.js viewer bundle inlined.
    // The geometry URL is injected as a JSON-encoded string into the global config script block.
    private static string BuildViewerHtml(IWebHostEnvironment env, string geometryUrl)
    {
        // JSON-encode the URL so it is safe to embed verbatim inside a JS string literal.
        var jsonUrl = System.Text.Json.JsonSerializer.Serialize(geometryUrl);

        // The template contains the placeholder as a bare token inside a script assignment:
        //   window.__CATALOG3D_GEOMETRY_URL__ = '{{GEOMETRY_URL}}';
        // We replace the entire right-hand side (including the single-quote delimiters that
        // mark the placeholder) with the JSON-serialized value (which carries its own quotes).
        var htmlBase = GetViewerHtmlBase(env);
        return htmlBase.Replace("'{{GEOMETRY_URL}}'", jsonUrl, StringComparison.Ordinal);
    }
}
