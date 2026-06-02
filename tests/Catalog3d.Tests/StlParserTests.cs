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
}
