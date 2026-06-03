using System.Net;
using System.Net.Http.Json;
using Catalog3d.Application.Abstractions;
using Catalog3d.Domain.Entities;
using Catalog3d.Domain.Enums;
using Catalog3d.Infrastructure.Persistence;
using Catalog3d.Infrastructure.Storage;
using Catalog3d.Web.Endpoints.Dto;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Catalog3d.Tests;

/// <summary>
/// Smoke tests for GET /api/v1/collections.
/// Uses WebApplicationFactory with an in-memory database to verify that the
/// ACL filtering correctly restricts results to collections the dev user is
/// assigned to, and that unassigned collections are hidden.
/// </summary>
public sealed class CollectionsEndpointTests : IClassFixture<CollectionsSmokeFixture>
{
    private readonly CollectionsSmokeFixture _fixture;

    public CollectionsEndpointTests(CollectionsSmokeFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetCollections_DevUser_ReturnsOnlyAuthorizedCollections()
    {
        // admin user (UserId = "admin") is assigned Preview to "public-collection"
        // and Admin to "admin-only-collection". "hidden-collection" has no assignment.
        var client = _fixture.CreateAuthenticatedClient("admin");
        var response = await client.GetAsync("/api/v1/collections");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var collections = await response.Content.ReadFromJsonAsync<CollectionDto[]>();
        Assert.NotNull(collections);

        var slugs = collections.Select(c => c.Slug).ToHashSet();
        Assert.Contains("public-collection", slugs);
        Assert.Contains("admin-only-collection", slugs);
        Assert.DoesNotContain("hidden-collection", slugs);
    }

    [Fact]
    public async Task GetCollections_ViewerUser_SeesOnlyOwnCollections()
    {
        // viewer is assigned Preview to "public-collection" only.
        var client = _fixture.CreateAuthenticatedClient("viewer");
        var response = await client.GetAsync("/api/v1/collections");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var collections = await response.Content.ReadFromJsonAsync<CollectionDto[]>();
        Assert.NotNull(collections);

        var slugs = collections.Select(c => c.Slug).ToHashSet();
        Assert.Contains("public-collection", slugs);
        Assert.DoesNotContain("admin-only-collection", slugs);
        Assert.DoesNotContain("hidden-collection", slugs);
    }

    [Fact]
    public async Task GetCollections_Unauthenticated_Returns401()
    {
        // No X-Dev-User header → not authenticated → authorization fails.
        var client = _fixture.CreateClient();
        var response = await client.GetAsync("/api/v1/collections");

        // RequireAuthorization() returns 401 for unauthenticated requests.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

/// <summary>
/// Focused fixture for upload-response field and Location header tests (M2).
/// Uses an isolated fixture so upload can be exercised without affecting smoke tests.
/// </summary>
public sealed class UploadResponseFieldTests : IAsyncLifetime
{
    private UploadResponseFixture? _fixture;

    public async Task InitializeAsync()
    {
        _fixture = new UploadResponseFixture();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_fixture is not null)
            await _fixture.DisposeAsync();
    }

    [Fact]
    public async Task Upload_LocationHeader_ContainsSlugNotGuid()
    {
        var client = _fixture!.CreateAuthenticatedClient("admin");

        using var content = BuildMinimalStlContent("slug-test.stl");
        var response = await client.PostAsync(
            $"/api/v1/collections/{UploadResponseFixture.CollectionSlug}/models", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var location = response.Headers.Location?.ToString();
        Assert.NotNull(location);
        Assert.Contains("slug-test", location);
        Assert.DoesNotMatch(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", location);
    }

    [Fact]
    public async Task Upload_ResponseBody_HasSlugField()
    {
        var client = _fixture!.CreateAuthenticatedClient("admin");

        using var content = BuildMinimalStlContent("my-model.stl");
        var response = await client.PostAsync(
            $"/api/v1/collections/{UploadResponseFixture.CollectionSlug}/models", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<UploadModelResponse>();
        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.Slug));
        Assert.Equal("my-model", result.Slug);
    }

    private static MultipartFormDataContent BuildMinimalStlContent(string fileName)
    {
        var buf = new byte[84 + 50];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(80, 4), 1u);
        var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(buf);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(fileContent, "file", fileName);
        return multipart;
    }
}

/// <summary>
/// Fixture for upload-field tests. Seeds one collection with admin having Uploader role,
/// provides a real DiskFileStore in a temp dir, and registers IModelUploadService.
/// </summary>
public sealed class UploadResponseFixture : WebApplicationFactory<Program>, IAsyncDisposable
{
    public static readonly Guid CollectionId = new("bbbbbbbb-0000-0000-0000-000000000001");
    public const string CollectionSlug = "upload-field-test";

    private readonly string _dbName = $"upload-field-{Guid.NewGuid():N}";
    private readonly string _storageRoot;

    public UploadResponseFixture()
    {
        _storageRoot = Path.Combine(Path.GetTempPath(), $"upload-field-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_storageRoot);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(DbContextOptions<CatalogDbContext>))
                    services.RemoveAt(i);
            }

            var dbOpts = new DbContextOptionsBuilder<CatalogDbContext>()
                .UseInMemoryDatabase(_dbName)
                .Options;
            services.AddSingleton(dbOpts);

            var root = _storageRoot;
            services.Configure<DiskFileStoreOptions>(o => o.Root = root);

            // The upload endpoint delegates to IModelUploadService; register it here
            // since Program.cs registers it in production but not in the test host.
            services.AddScoped<IModelUploadService, Catalog3d.Infrastructure.Upload.ModelUploadService>();

            services.RemoveAll<IHostedService>();
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        SeedTestData(db);

        return host;
    }

    public HttpClient CreateAuthenticatedClient(string username)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-User", username);
        return client;
    }

    private static void SeedTestData(CatalogDbContext db)
    {
        var now = DateTimeOffset.UtcNow;

        db.Collections.Add(new Collection
        {
            Id = CollectionId,
            Slug = CollectionSlug,
            Name = "Upload Field Test",
            Description = "",
            CreatedAt = now,
            UpdatedAt = now,
        });

        // admin user gets Uploader role so they can post.
        db.RoleAssignments.Add(new RoleAssignment
        {
            CollectionId = CollectionId,
            Principal = "admin",
            Role = CollectionRole.Uploader,
        });

        db.SaveChanges();
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        Dispose();
        if (Directory.Exists(_storageRoot))
        {
            try { Directory.Delete(_storageRoot, recursive: true); }
            catch (IOException) { }
        }
        await ValueTask.CompletedTask;
    }
}

/// <summary>
/// Shared fixture: one WebApplicationFactory instance per test class run.
/// Swaps CatalogDbContext for an in-memory database and seeds predictable test data.
/// </summary>
public sealed class CollectionsSmokeFixture : WebApplicationFactory<Program>
{
    // Stable GUIDs for seed entities so tests are deterministic.
    public static readonly Guid PublicCollectionId = new("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid AdminCollectionId = new("aaaaaaaa-0000-0000-0000-000000000002");
    public static readonly Guid HiddenCollectionId = new("aaaaaaaa-0000-0000-0000-000000000003");

    // Unique database name ensures test isolation across fixture instances.
    private readonly string _dbName = $"catalog-smoke-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Replace the Npgsql DbContext with an in-memory one.
            // Must remove ALL descriptors whose ServiceType is DbContextOptions<T>
            // because EF Core checks that only one provider is registered per
            // internal service provider instance.
            for (var i = services.Count - 1; i >= 0; i--)
            {
                var d = services[i];
                if (d.ServiceType == typeof(DbContextOptions<CatalogDbContext>))
                    services.RemoveAt(i);
            }

            // Register a pre-built Options instance as Singleton. This bypasses
            // the normal AddDbContext<T> path that would trigger provider detection
            // against other registered services, and guarantees a single clean
            // InMemory-only DbContextOptions<CatalogDbContext> in the container.
            var inMemoryOptions = new DbContextOptionsBuilder<CatalogDbContext>()
                .UseInMemoryDatabase(_dbName)
                .Options;

            services.AddSingleton(inMemoryOptions);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        // Seed after the host is fully built to use its DI scope.
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            SeedTestData(db);
        }

        return host;
    }

    public HttpClient CreateAuthenticatedClient(string username)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-User", username);
        return client;
    }

    private static void SeedTestData(CatalogDbContext db)
    {
        var now = DateTimeOffset.UtcNow;

        db.Collections.AddRange(
            new Collection
            {
                Id = PublicCollectionId,
                Slug = "public-collection",
                Name = "Public Collection",
                Description = "Visible to admin and viewer",
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Collection
            {
                Id = AdminCollectionId,
                Slug = "admin-only-collection",
                Name = "Admin Only Collection",
                Description = "Visible only to admin",
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Collection
            {
                Id = HiddenCollectionId,
                Slug = "hidden-collection",
                Name = "Hidden Collection",
                Description = "Not visible to any dev user",
                CreatedAt = now,
                UpdatedAt = now,
            });

        // admin (UserId="admin") → Preview on public, Admin on admin-only.
        // viewer (UserId="viewer") → Preview on public only.
        // hidden-collection has no assignments → invisible to everyone.
        db.RoleAssignments.AddRange(
            new RoleAssignment
            {
                CollectionId = PublicCollectionId,
                Principal = "admin",
                Role = CollectionRole.Preview,
            },
            new RoleAssignment
            {
                CollectionId = AdminCollectionId,
                Principal = "admin",
                Role = CollectionRole.Admin,
            },
            new RoleAssignment
            {
                CollectionId = PublicCollectionId,
                Principal = "viewer",
                Role = CollectionRole.Preview,
            });

        db.SaveChanges();
    }
}
