using System.Buffers.Binary;
using System.Text.Json;
using Catalog3d.Infrastructure.Rendering;

namespace Catalog3d.Tests;

/// <summary>
/// Tests for BinaryStlParser against programmatically generated binary STL streams.
///
/// Binary STL layout (per spec):
///   80-byte header (arbitrary ASCII)
///   4-byte LE uint32 triangle count
///   per triangle, 50 bytes:
///     12 bytes — float32[3] normal vector (ignored by parser)
///     12 bytes — float32[3] vertex A
///     12 bytes — float32[3] vertex B
///     12 bytes — float32[3] vertex C
///     2 bytes  — uint16 attribute byte count (ignored)
/// </summary>
public sealed class StlParserTests
{
    // -------------------------------------------------------------------------
    // 1. Single-triangle STL — triangle count and bounding box
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_SingleTriangle_ReturnsTriangleCountOne()
    {
        // Unit triangle in the XY plane: (0,0,0), (1,0,0), (0,1,0)
        var stl = BuildBinaryStl([
            new Triangle(
                Normal: (0f, 0f, 1f),
                A: (0f, 0f, 0f),
                B: (1f, 0f, 0f),
                C: (0f, 1f, 0f))
        ]);

        using var stream = new MemoryStream(stl);
        var result = await BinaryStlParser.ParseAsync(stream, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result.Value.TriCount);
    }

    [Fact]
    public async Task ParseAsync_SingleTriangle_BoundingBoxMatchesVertices()
    {
        // Triangle spans (0,0,0) → (3,2,1)
        var stl = BuildBinaryStl([
            new Triangle(
                Normal: (0f, 0f, 1f),
                A: (0f, 0f, 0f),
                B: (3f, 0f, 0f),
                C: (0f, 2f, 1f))
        ]);

        using var stream = new MemoryStream(stl);
        var result = await BinaryStlParser.ParseAsync(stream, CancellationToken.None);

        Assert.NotNull(result);
        var bbox = ParseBoundingBox(result.Value.BoundingBoxJson);

        Assert.Equal(0.0, bbox.MinX, precision: 5);
        Assert.Equal(0.0, bbox.MinY, precision: 5);
        Assert.Equal(0.0, bbox.MinZ, precision: 5);
        Assert.Equal(3.0, bbox.MaxX, precision: 5);
        Assert.Equal(2.0, bbox.MaxY, precision: 5);
        Assert.Equal(1.0, bbox.MaxZ, precision: 5);
    }

    // -------------------------------------------------------------------------
    // 2. Multi-triangle STL — aggregate bounding box is the AABB of all vertices
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_TwoTriangles_TriangleCountIsTwo()
    {
        var stl = BuildBinaryStl([
            new Triangle((0f, 0f, 1f), (0f, 0f, 0f), (1f, 0f, 0f), (0f, 1f, 0f)),
            new Triangle((0f, 0f, 1f), (5f, 5f, 5f), (6f, 5f, 5f), (5f, 6f, 5f)),
        ]);

        using var stream = new MemoryStream(stl);
        var result = await BinaryStlParser.ParseAsync(stream, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result.Value.TriCount);
    }

    [Fact]
    public async Task ParseAsync_TwoTriangles_BoundingBoxSpansBothTriangles()
    {
        // Triangle 1 lives near origin; triangle 2 is offset.
        var stl = BuildBinaryStl([
            new Triangle((0f, 0f, 1f), (0f,  0f, 0f), (1f, 0f, 0f), (0f, 1f, 0f)),
            new Triangle((0f, 0f, 1f), (-1f, -2f, -3f), (10f, 0f, 0f), (0f, 0f, 7f)),
        ]);

        using var stream = new MemoryStream(stl);
        var result = await BinaryStlParser.ParseAsync(stream, CancellationToken.None);

        Assert.NotNull(result);
        var bbox = ParseBoundingBox(result.Value.BoundingBoxJson);

        Assert.Equal(-1.0, bbox.MinX, precision: 5);
        Assert.Equal(-2.0, bbox.MinY, precision: 5);
        Assert.Equal(-3.0, bbox.MinZ, precision: 5);
        Assert.Equal(10.0, bbox.MaxX, precision: 5);
        Assert.Equal(1.0,  bbox.MaxY, precision: 5);
        Assert.Equal(7.0,  bbox.MaxZ, precision: 5);
    }

    // -------------------------------------------------------------------------
    // 3. Zero-triangle STL — edge case
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_ZeroTriangles_ReturnsZeroCountAndZeroBbox()
    {
        var stl = BuildBinaryStl([]);

        using var stream = new MemoryStream(stl);
        var result = await BinaryStlParser.ParseAsync(stream, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0, result.Value.TriCount);

        var bbox = ParseBoundingBox(result.Value.BoundingBoxJson);
        Assert.Equal(0.0, bbox.MinX, precision: 5);
        Assert.Equal(0.0, bbox.MaxX, precision: 5);
    }

    // -------------------------------------------------------------------------
    // 4. Malformed / too-short stream returns null
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_EmptyStream_ReturnsNull()
    {
        using var stream = new MemoryStream([]);
        var result = await BinaryStlParser.ParseAsync(stream, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ParseAsync_TruncatedHeader_ReturnsNull()
    {
        // Only 40 bytes — less than the 84-byte minimum (80 header + 4 count)
        using var stream = new MemoryStream(new byte[40]);
        var result = await BinaryStlParser.ParseAsync(stream, CancellationToken.None);

        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // 5. Triangle count embedded in header matches the field exactly
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_FiveTriangles_TriangleCountIsFive()
    {
        var tris = new Triangle[5];
        for (var i = 0; i < 5; i++)
        {
            float f = i;
            tris[i] = new Triangle((0f, 0f, 1f), (f, 0f, 0f), (f + 1f, 0f, 0f), (f, 1f, 0f));
        }

        var stl = BuildBinaryStl(tris);
        using var stream = new MemoryStream(stl);
        var result = await BinaryStlParser.ParseAsync(stream, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(5, result.Value.TriCount);
    }

    // -------------------------------------------------------------------------
    // 6. Multi-chunk read that drains buffer to exactly zero (H4 regression)
    //
    // The parser's shift-to-front guard is `if (buffered > 0 && offset > 0)`.
    // When the inner loop drains the buffer to exactly 0, `offset` is non-zero
    // but the guard fires false — `offset` is never reset. On the next outer
    // iteration a fresh read fills `buffer[0..]` but vertex reads use the stale
    // `offset` → wrong bounding-box coordinates.
    //
    // Force the scenario with a throttled stream that delivers exactly one
    // triangle stride (50 bytes) per ReadAsync call. With 3 distinct triangles
    // the inner loop drains to 0 after each, exercising the buggy path twice.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_MultiChunkReadDrainingToZero_CorrectBoundingBox()
    {
        // Three triangles with well-separated vertices so a wrong offset
        // would produce an obviously wrong bounding box.
        // Triangle 1: all vertices at y=0 plane, x=[0,1], z=[0,0]
        // Triangle 2: all vertices at y=10 plane, x=[20,21], z=[5,5]
        // Triangle 3: all vertices at negative coords
        var triangles = new Triangle[]
        {
            new((0f, 0f, 1f), (0f, 0f, 0f),  (1f, 0f, 0f),  (0f, 0f, 0f)),
            new((0f, 0f, 1f), (20f, 10f, 5f), (21f, 10f, 5f), (20f, 10f, 5f)),
            new((0f, 0f, 1f), (-5f, -3f, -2f), (-4f, -3f, -2f), (-5f, -3f, -2f)),
        };
        var stl = BuildBinaryStl(triangles);

        // One-stride-at-a-time stream forces the outer loop to iterate once per
        // triangle and drains buffered to 0 after each inner-loop pass.
        using var stream = new ThrottledStream(stl, chunkSize: 50);
        var result = await BinaryStlParser.ParseAsync(stream, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, result.Value.TriCount);

        var bbox = ParseBoundingBox(result.Value.BoundingBoxJson);

        Assert.Equal(-5.0, bbox.MinX, precision: 5);
        Assert.Equal(-3.0, bbox.MinY, precision: 5);
        Assert.Equal(-2.0, bbox.MinZ, precision: 5);
        Assert.Equal(21.0, bbox.MaxX, precision: 5);
        Assert.Equal(10.0, bbox.MaxY, precision: 5);
        Assert.Equal(5.0,  bbox.MaxZ, precision: 5);
    }

    // -------------------------------------------------------------------------
    // 6. BoundingBox JSON structure validation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ParseAsync_BoundingBoxJson_HasMinAndMaxArraysOfThree()
    {
        var stl = BuildBinaryStl([
            new Triangle((0f, 0f, 1f), (1f, 2f, 3f), (4f, 5f, 6f), (7f, 8f, 9f))
        ]);

        using var stream = new MemoryStream(stl);
        var result = await BinaryStlParser.ParseAsync(stream, CancellationToken.None);

        Assert.NotNull(result);

        using var doc = JsonDocument.Parse(result.Value.BoundingBoxJson);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("min", out var min));
        Assert.True(root.TryGetProperty("max", out var max));
        Assert.Equal(3, min.GetArrayLength());
        Assert.Equal(3, max.GetArrayLength());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private readonly record struct Triangle(
        (float X, float Y, float Z) Normal,
        (float X, float Y, float Z) A,
        (float X, float Y, float Z) B,
        (float X, float Y, float Z) C);

    /// <summary>Builds a minimal valid binary STL byte array from the given triangles.</summary>
    private static byte[] BuildBinaryStl(ReadOnlySpan<Triangle> triangles)
    {
        const int headerSize = 80;
        const int triCountSize = 4;
        const int triStride = 50;

        int totalSize = headerSize + triCountSize + triangles.Length * triStride;
        var buf = new byte[totalSize];

        // Header: 80 bytes of zeros (ASCII null is valid).
        // Triangle count at offset 80.
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(headerSize, triCountSize), (uint)triangles.Length);

        int pos = headerSize + triCountSize;
        foreach (var tri in triangles)
        {
            WriteFloat3(buf, ref pos, tri.Normal.X, tri.Normal.Y, tri.Normal.Z); // normal
            WriteFloat3(buf, ref pos, tri.A.X, tri.A.Y, tri.A.Z);               // vertex A
            WriteFloat3(buf, ref pos, tri.B.X, tri.B.Y, tri.B.Z);               // vertex B
            WriteFloat3(buf, ref pos, tri.C.X, tri.C.Y, tri.C.Z);               // vertex C
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos, 2), 0);    // attr
            pos += 2;
        }

        return buf;
    }

    private static void WriteFloat3(byte[] buf, ref int pos, float x, float y, float z)
    {
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(pos, 4), x); pos += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(pos, 4), y); pos += 4;
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(pos, 4), z); pos += 4;
    }

    private readonly record struct BBox(
        double MinX, double MinY, double MinZ,
        double MaxX, double MaxY, double MaxZ);

    private static BBox ParseBoundingBox(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var min = root.GetProperty("min");
        var max = root.GetProperty("max");

        return new BBox(
            min[0].GetDouble(), min[1].GetDouble(), min[2].GetDouble(),
            max[0].GetDouble(), max[1].GetDouble(), max[2].GetDouble());
    }

    /// <summary>
    /// Wraps a byte array and returns at most <paramref name="chunkSize"/> bytes per
    /// ReadAsync call. Forces the parser into multi-iteration outer-loop paths.
    /// </summary>
    private sealed class ThrottledStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= data.Length) return 0;
            int toCopy = Math.Min(Math.Min(count, chunkSize), data.Length - _position);
            data.AsSpan(_position, toCopy).CopyTo(buffer.AsSpan(offset, toCopy));
            _position += toCopy;
            return toCopy;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position >= data.Length) return new ValueTask<int>(0);
            int toCopy = Math.Min(Math.Min(buffer.Length, chunkSize), data.Length - _position);
            data.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return new ValueTask<int>(toCopy);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
