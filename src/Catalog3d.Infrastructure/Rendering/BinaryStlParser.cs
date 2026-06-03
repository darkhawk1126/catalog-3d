using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace Catalog3d.Infrastructure.Rendering;

/// <summary>
/// Parses binary STL files to extract triangle count and axis-aligned bounding box.
/// Operates on the blob stream directly; no intermediate file copy.
///
/// Binary STL layout:
///   [0..79]  80-byte ASCII header (ignored)
///   [80..83] uint32 LE triangle count
///   per triangle (50 bytes):
///     [0..11]  float32[3] normal   (ignored)
///     [12..23] float32[3] vertex A
///     [24..35] float32[3] vertex B
///     [36..47] float32[3] vertex C
///     [48..49] uint16 attribute byte count (ignored)
/// </summary>
internal static class BinaryStlParser
{
    private const int HeaderSize = 80;
    private const int TriCountSize = 4;
    private const int TriangleStride = 50; // 12 normal + 3×12 vertices + 2 attr
    private const int VertexOffset = 12;   // skip normal, land on vertex A
    private const int ReadBufferSize = 80 * 1024; // 80 KiB — below LOH threshold

    /// <summary>
    /// Reads the binary STL from <paramref name="stream"/>, computes triangle count and
    /// bounding box. Returns null on malformed or ASCII STL (non-binary format).
    /// Caller owns the stream lifetime.
    /// </summary>
    internal static async Task<StlMetadata?> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        // Read header + count (84 bytes) in one shot.
        byte[] header = ArrayPool<byte>.Shared.Rent(HeaderSize + TriCountSize);
        try
        {
            int read = await ReadExactAsync(stream, header, HeaderSize + TriCountSize, cancellationToken)
                .ConfigureAwait(false);

            if (read < HeaderSize + TriCountSize)
                return null; // Too short to be binary STL.

            uint triCount = BinaryPrimitives.ReadUInt32LittleEndian(
                header.AsSpan(HeaderSize, TriCountSize));

            if (triCount == 0)
                return new StlMetadata(0, BoundingBoxJson(0f, 0f, 0f, 0f, 0f, 0f));

            long expectedSize = (long)triCount * TriangleStride;

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
            try
            {
                // Bytes remaining to consume across all triangles.
                long remaining = expectedSize;
                int buffered = 0; // valid bytes in buffer
                int offset = 0;   // read head within buffer

                while (remaining > 0)
                {
                    // Shift unconsumed bytes to front, then fill.
                    // Reset when buffer is fully drained (buffered == 0) even though
                    // no copy is needed — offset must be zeroed so the next read lands
                    // at buffer[0] and vertex reads within the inner loop use the
                    // correct base. Without this, a drain-to-exactly-zero leaves a
                    // stale offset that corrupts bounding-box reads for subsequent chunks.
                    if (buffered == 0)
                    {
                        offset = 0;
                    }
                    else if (offset > 0)
                    {
                        buffer.AsSpan(offset, buffered).CopyTo(buffer.AsSpan(0, buffered));
                        offset = 0;
                    }

                    int toRead = Math.Min(buffer.Length - buffered, (int)Math.Min(remaining, buffer.Length - buffered));
                    if (toRead > 0)
                    {
                        int n = await stream.ReadAsync(buffer.AsMemory(buffered, toRead), cancellationToken)
                            .ConfigureAwait(false);
                        if (n == 0) break; // Unexpected EOF.
                        buffered += n;
                    }

                    // Consume complete triangles from buffer.
                    while (buffered >= TriangleStride && remaining > 0)
                    {
                        // Each vertex: 3×float32 LE.
                        for (int v = 0; v < 3; v++)
                        {
                            int vOff = offset + VertexOffset + v * 12;
                            float x = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(vOff, 4));
                            float y = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(vOff + 4, 4));
                            float z = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(vOff + 8, 4));

                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                            if (z < minZ) minZ = z;
                            if (z > maxZ) maxZ = z;
                        }

                        offset += TriangleStride;
                        buffered -= TriangleStride;
                        remaining -= TriangleStride;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            // If we never updated the min/max (e.g., read error mid-stream), guard with defaults.
            if (minX == float.MaxValue)
            {
                minX = minY = minZ = 0f;
                maxX = maxY = maxZ = 0f;
            }

            return new StlMetadata((int)triCount, BoundingBoxJson(minX, minY, minZ, maxX, maxY, maxZ));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    // Produces {"min":[x,y,z],"max":[x,y,z]} with 6 decimal places for adequate precision.
    private static string BoundingBoxJson(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
    {
        return JsonSerializer.Serialize(new
        {
            min = new[] { Round(minX), Round(minY), Round(minZ) },
            max = new[] { Round(maxX), Round(maxY), Round(maxZ) }
        });

        static double Round(float v) => Math.Round((double)v, 6);
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes or until EOF.</summary>
    private static async Task<int> ReadExactAsync(
        Stream stream,
        byte[] buffer,
        int count,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken)
                .ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}

/// <summary>Parsed STL metrics for persistence on ModelFile.</summary>
internal readonly record struct StlMetadata(int TriCount, string BoundingBoxJson);
