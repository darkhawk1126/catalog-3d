using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Catalog3d.Application.Abstractions;
using Catalog3d.Domain.Entities;
using Catalog3d.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Catalog3d.Infrastructure.Upload;

/// <summary>
/// Shared upload pipeline consumed by both the REST endpoint and the Blazor UI.
/// Thread-safe: all state is request-scoped (no instance fields mutated after ctor).
/// </summary>
internal sealed class ModelUploadService(
    IFileStore fileStore,
    IRenderQueue renderQueue,
    CatalogDbContext db,
    IOptions<ModelUploadOptions> options,
    ILogger<ModelUploadService> logger) : IModelUploadService
{
    // Binary-STL layout constants.
    private const int HeaderSize = 80;
    private const int TriCountOffset = 80;
    private const int TriCountSize = 4;
    private const int MinStlSize = HeaderSize + TriCountSize; // 84 bytes
    private const int TriangleStride = 50;
    private const string StlMimeType = "model/stl";

    public async Task<ModelUploadResult> UploadAsync(
        ModelUploadRequest request,
        CancellationToken ct = default)
    {
        long maxBytes = options.Value.MaxSizeBytes;

        // --- 1. Derive slug early so we can reject empty slugs before touching storage ---
        string slug = string.IsNullOrEmpty(request.SlugOverride)
            ? SlugFromFileName(request.FileName)
            : request.SlugOverride.Trim();

        if (string.IsNullOrEmpty(slug))
            return new ModelUploadResult.InvalidSlug(
                $"Could not derive a non-empty slug from filename '{request.FileName}'.");

        // --- 2. Stream to a counting/buffering wrapper that captures the first 84 bytes ---
        //        for STL header validation, while still streaming to IFileStore.
        string blobKey;
        byte[] headerBuffer = new byte[MinStlSize];
        int headerRead = 0;

        using var counted = new CountingStream(request.FileStream, maxBytes);
        try
        {
            // Tee: copy the head bytes into headerBuffer as the stream passes through.
            using var teeStream = new TeeHeadStream(counted, headerBuffer, MinStlSize, onRead: n => headerRead += n);
            blobKey = await fileStore.WriteAsync(teeStream, "blobs", ct).ConfigureAwait(false);
        }
        catch (UploadTooLargeException)
        {
            return new ModelUploadResult.TooLarge(maxBytes);
        }

        // --- 3. Validate binary-STL signature ---
        if (headerRead < MinStlSize)
        {
            await DeleteBlobIfUnreferencedAsync(blobKey, ct).ConfigureAwait(false);
            return new ModelUploadResult.InvalidContent(
                $"File is too short ({headerRead} bytes); binary STL requires at least {MinStlSize} bytes.");
        }

        uint triCount = BinaryPrimitives.ReadUInt32LittleEndian(
            headerBuffer.AsSpan(TriCountOffset, TriCountSize));

        long expectedFileSize = MinStlSize + (long)triCount * TriangleStride;
        long actualSize = await fileStore.SizeAsync(blobKey, "blobs", ct).ConfigureAwait(false);

        if (actualSize != expectedFileSize)
        {
            await DeleteBlobIfUnreferencedAsync(blobKey, ct).ConfigureAwait(false);
            return new ModelUploadResult.InvalidContent(
                $"Binary STL declares {triCount} triangles (expected {expectedFileSize} bytes) " +
                $"but file is {actualSize} bytes.");
        }

        // --- 4. Persist Model + ModelFile, handle slug conflict ---

        // Optimistic pre-check: fast path for the common serial case (catches the slug
        // collision without relying on Postgres error codes). A concurrent duplicate that
        // slips past here is still caught by the unique-constraint path below.
        bool slugExists = await db.Models
            .AnyAsync(m => m.Slug == slug, ct)
            .ConfigureAwait(false);

        if (slugExists)
        {
            await DeleteBlobIfUnreferencedAsync(blobKey, ct).ConfigureAwait(false);
            logger.LogWarning("Upload rejected: slug '{Slug}' already exists (pre-check).", slug);
            return new ModelUploadResult.SlugConflict(slug);
        }

        var now = DateTimeOffset.UtcNow;

        var model = new Model
        {
            Id = Guid.NewGuid(),
            CollectionId = request.CollectionId,
            Slug = slug,
            Name = string.IsNullOrWhiteSpace(request.ModelName) ? slug : request.ModelName,
            Description = request.Description ?? string.Empty,
            Owner = request.OwnerId,
            Status = ModelStatus.Processing,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var modelFile = new ModelFile
        {
            Id = Guid.NewGuid(),
            ModelId = model.Id,
            Kind = ModelFileKind.Stl,
            BlobKey = blobKey,
            Size = actualSize,
            MimeType = StlMimeType,
            Sha256 = blobKey, // BlobKey IS the SHA-256 hex.
            RenderStatus = RenderStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Models.Add(model);
        db.ModelFiles.Add(modelFile);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Slug already exists — clean up the orphaned blob and report the conflict.
            db.Models.Remove(model);
            db.ModelFiles.Remove(modelFile);
            await DeleteBlobIfUnreferencedAsync(blobKey, ct).ConfigureAwait(false);

            logger.LogWarning("Upload rejected: slug '{Slug}' already exists.", slug);
            return new ModelUploadResult.SlugConflict(slug);
        }

        // --- 5. Enqueue render ---
        await renderQueue.EnqueueAsync(modelFile.Id, ct).ConfigureAwait(false);

        return new ModelUploadResult.Success(model, modelFile);
    }

    // Derive a slug from a filename: lowercase, strip extension, replace non-alnum with dash, trim dashes.
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

    // Content-addressed blobs are deduplicated, so one key can back multiple models. During
    // cleanup of a failed/rejected upload, only delete the blob when no other ModelFile still
    // references it — otherwise a duplicate-content upload that hits a slug conflict (or fails
    // validation) would orphan a sibling model that shares the identical blob.
    private async Task DeleteBlobIfUnreferencedAsync(string blobKey, CancellationToken ct)
    {
        bool stillReferenced = await db.ModelFiles
            .AnyAsync(f => f.BlobKey == blobKey, ct)
            .ConfigureAwait(false);

        if (stillReferenced)
        {
            logger.LogDebug(
                "Retained content-addressed blob {BlobKey}; still referenced by another model.",
                blobKey);
            return;
        }

        await fileStore.DeleteAsync(blobKey, "blobs", ct).ConfigureAwait(false);
    }

    // Postgres unique-constraint violation code is "23505"; EF wraps it in DbUpdateException.
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        var inner = ex.InnerException;
        while (inner is not null)
        {
            // Npgsql surfaces SqlState on NpgsqlException; check type name to avoid a hard
            // dependency on the Npgsql assembly in this method (Infrastructure already refs it,
            // but the EF InMemory provider used in tests does not surface PostgresException).
            if (inner.GetType().Name == "PostgresException")
            {
                // SqlState property exists; read via reflection to avoid hard type reference.
                var sqlState = inner.GetType().GetProperty("SqlState")?.GetValue(inner) as string;
                return sqlState == "23505";
            }
            inner = inner.InnerException;
        }
        return false;
    }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// <summary>Thrown internally when the byte ceiling is exceeded during streaming.</summary>
internal sealed class UploadTooLargeException : Exception
{
    public UploadTooLargeException() : base("Upload exceeded the configured size limit.") { }
}

/// <summary>
/// Wraps an inner stream and throws <see cref="UploadTooLargeException"/> after
/// <paramref name="maxBytes"/> have been read.
/// </summary>
internal sealed class CountingStream(Stream inner, long maxBytes) : Stream
{
    private long _totalRead;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = inner.Read(buffer);
        AccountRead(n);
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int n = await inner.ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
        AccountRead(n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int n = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        AccountRead(n);
        return n;
    }

    private void AccountRead(int n)
    {
        if (n <= 0) return;
        _totalRead += n;
        if (_totalRead > maxBytes)
            throw new UploadTooLargeException();
    }
}

/// <summary>
/// Wraps an inner stream and copies the first <paramref name="headBytes"/> bytes into
/// <paramref name="head"/>, invoking <paramref name="onRead"/> with the count copied on
/// each read. Subsequent reads pass through without copying.
/// </summary>
internal sealed class TeeHeadStream(
    Stream inner,
    byte[] head,
    int headBytes,
    Action<int> onRead) : Stream
{
    private int _headWritten;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = inner.Read(buffer);
        TeeHead(buffer[..n]);
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int n = await inner.ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
        TeeHead(buffer.AsSpan(offset, n));
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int n = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        TeeHead(buffer.Span[..n]);
        return n;
    }

    private void TeeHead(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || _headWritten >= headBytes) return;
        int toCopy = Math.Min(data.Length, headBytes - _headWritten);
        data[..toCopy].CopyTo(head.AsSpan(_headWritten, toCopy));
        _headWritten += toCopy;
        onRead(toCopy);
    }
}
