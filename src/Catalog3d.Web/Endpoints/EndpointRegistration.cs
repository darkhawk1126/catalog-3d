using Catalog3d.Application.Abstractions;
using Catalog3d.Domain.Entities;
using Catalog3d.Domain.Enums;
using Catalog3d.Infrastructure.Persistence;
using Catalog3d.Web.Endpoints.Dto;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
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
        collections.MapPost("/{slug}/models", UploadModelAsync)
            .RequireAuthorization()
            .DisableAntiforgery();

        // Models
        var models = v1.MapGroup("/models");

        // GET /api/v1/models/{slug}
        models.MapGet("/{slug}", GetModelBySlugAsync)
            .RequireAuthorization();

        // Viewer
        // GET /viewer/{fileId} — milestone 3

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
    // Uploader-gated. Streams multipart body directly to IFileStore — never buffers.
    // Reads: "name" and "description" form fields + "file" file part.
    // -------------------------------------------------------------------------
    private static async Task<IResult> UploadModelAsync(
        string slug,
        HttpContext httpContext,
        [FromServices] ICollectionAuthorizationService authService,
        [FromServices] IUserContext userContext,
        [FromServices] CatalogDbContext db,
        [FromServices] IFileStore fileStore,
        CancellationToken cancellationToken)
    {
        var collection = await db.Collections
            .FirstOrDefaultAsync(c => c.Slug == slug, cancellationToken)
            .ConfigureAwait(false);

        if (collection is null)
            return Results.NotFound();

        var hasAccess = await authService
            .AuthorizeAsync(collection.Id, userContext, CollectionRole.Uploader, cancellationToken)
            .ConfigureAwait(false);

        if (!hasAccess)
            return Results.Forbid();

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

        // Disable request body size limit for this endpoint — streaming, no buffer cap.
        var bodySizeFeature = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is not null)
            bodySizeFeature.MaxRequestBodySize = null;

        // Parse the multipart body without buffering file content to disk or memory.
        var reader = new MultipartReader(boundary, httpContext.Request.Body);

        string? modelName = null;
        string? modelDescription = null;
        string? modelSlugOverride = null;
        UploadModelResponse? uploadResult = null;

        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                continue;

            if (disposition.IsFileDisposition())
            {
                // Stream directly to IFileStore — no intermediate buffering.
                var blobKey = await fileStore
                    .WriteAsync(section.Body, "blobs", cancellationToken)
                    .ConfigureAwait(false);

                // Persist model + file record.
                var now = DateTimeOffset.UtcNow;
                var resolvedSlug = modelSlugOverride
                    ?? SlugFromFileName(disposition.FileName.Value ?? "upload");

                var model = new Model
                {
                    Id = Guid.NewGuid(),
                    CollectionId = collection.Id,
                    Slug = resolvedSlug,
                    Name = modelName ?? resolvedSlug,
                    Description = modelDescription ?? string.Empty,
                    Owner = userContext.UserId,
                    Status = ModelStatus.Processing,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                var mimeType = section.ContentType ?? "application/octet-stream";

                var modelFile = new ModelFile
                {
                    Id = Guid.NewGuid(),
                    ModelId = model.Id,
                    Kind = ModelFileKind.Stl,
                    BlobKey = blobKey,
                    Size = 0,       // Size is computed inside DiskFileStore; 0 is placeholder until render sidecar updates it.
                    MimeType = mimeType,
                    Sha256 = blobKey, // BlobKey is the SHA-256 hex — reuse it here.
                    RenderStatus = RenderStatus.Pending,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                db.Models.Add(model);
                db.ModelFiles.Add(modelFile);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                uploadResult = new UploadModelResponse(
                    model.Id,
                    modelFile.Id,
                    blobKey,
                    modelFile.Size,
                    blobKey);

                break; // Only process the first file part.
            }
            else if (disposition.IsFormDisposition())
            {
                var fieldName = disposition.Name.Value;
                using var sr = new StreamReader(section.Body);
                var value = await sr.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                modelName = fieldName?.Equals("name", StringComparison.OrdinalIgnoreCase) == true
                    ? value
                    : modelName;

                modelDescription = fieldName?.Equals("description", StringComparison.OrdinalIgnoreCase) == true
                    ? value
                    : modelDescription;

                modelSlugOverride = fieldName?.Equals("slug", StringComparison.OrdinalIgnoreCase) == true
                    ? value
                    : modelSlugOverride;
            }
        }

        if (uploadResult is null)
            return Results.BadRequest("No file part found in multipart body.");

        return Results.Created(
            $"/api/v1/models/{uploadResult.ModelId}",
            uploadResult);
    }

    // Derive a slug from a filename: lowercase, strip extension, replace non-alnum with dash.
    private static string SlugFromFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var chars = name.ToLowerInvariant().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]))
                chars[i] = '-';
        }
        return new string(chars).Trim('-');
    }
}
