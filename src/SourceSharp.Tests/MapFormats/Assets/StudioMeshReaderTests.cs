//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.Tests.AssetDb;
using Xunit;

namespace SourceSharp.Tests.MapFormats.Assets;

/// <summary>
/// Facts for <see cref="StudioMeshReader"/>: what each triangle carries, how
/// strips unpack, and which bodygroup model each <see cref="StudioBodyChoice"/>
/// reads.
/// </summary>
public class StudioMeshReaderTests
{
    private static readonly Vec3[] Quad =
    [
        new(0, 0, 0),
        new(10, 0, 0),
        new(0, 10, 0),
        new(10, 10, 0),
    ];

    private static IReadOnlyList<StudioTriangle> Read(
        (byte[] Mdl, byte[] Vvd, byte[] Vtx) files,
        StudioBodyChoice bodies = StudioBodyChoice.FirstDrawn) =>
        StudioMeshReader.Lod0Triangles(
            MdlFile.Parse(files.Mdl), VvdFile.Parse(files.Vvd), VtxFile.Parse(files.Vtx), bodies);

    [Fact]
    public void ATriangleCarriesPositionsNormalsUvsAndSlot()
    {
        Vec3[] normals = [new(0, 0, 1), new(0, 1, 0), new(1, 0, 0), new(0, 0, -1)];
        (float U, float V)[] uvs = [(0.1f, 0.2f), (0.3f, 0.4f), (0.5f, 0.6f), (0.7f, 0.8f)];

        StudioTriangle triangle = Assert.Single(Read(StudioTestModel.Build(Quad, [0, 1, 3], uvs: uvs, normals: normals)));

        Assert.Equal(Quad[0], triangle.A);
        Assert.Equal(Quad[1], triangle.B);
        Assert.Equal(Quad[3], triangle.C);
        Assert.Equal(normals[0], triangle.NormalA);
        Assert.Equal(normals[1], triangle.NormalB);
        Assert.Equal(normals[3], triangle.NormalC);
        Assert.Equal(uvs[0], triangle.UvA);
        Assert.Equal(uvs[3], triangle.UvC);
        Assert.Equal(0, triangle.Slot);
    }

    [Fact]
    public void ATriangleListIsReadThreeIndicesAtATimeAndAPartialTripleIsIgnored()
    {
        IReadOnlyList<StudioTriangle> triangles = Read(StudioTestModel.Build(Quad, [0, 1, 2, 1, 3, 2, 0]));

        Assert.Equal(2, triangles.Count);
        Assert.Equal((Quad[1], Quad[3], Quad[2]), (triangles[1].A, triangles[1].B, triangles[1].C));
    }

    [Fact]
    public void AStripFlipsEverySecondTriangleSoTheWindingStaysContinuous()
    {
        // Strip 0,1,2,3: triangles (0,1,2) and (1,2,3) read as (1,3,2), so both
        // face the same way.
        IReadOnlyList<StudioTriangle> triangles = Read(StudioTestModel.Build(Quad, [0, 1, 2, 3], triStrip: true));

        Assert.Equal(2, triangles.Count);
        Assert.Equal((Quad[0], Quad[1], Quad[2]), (triangles[0].A, triangles[0].B, triangles[0].C));
        Assert.Equal((Quad[1], Quad[3], Quad[2]), (triangles[1].A, triangles[1].B, triangles[1].C));
    }

    [Fact]
    public void IndicesPastTheGroupOrTheVertexBlockAreDropped()
    {
        Assert.Single(Read(StudioTestModel.Build(Quad, [0, 1, 2, 0, 1, 9])));
    }

    [Fact]
    public void FirstDrawnSkipsAnEmptyOffModelToTheRealOne()
    {
        IReadOnlyList<StudioTriangle> triangles = Read(
            StudioTestModel.Build(Quad, [0, 1, 2], leadingEmptyModel: true), StudioBodyChoice.FirstDrawn);

        Assert.Single(triangles);
    }

    [Fact]
    public void DefaultDrawsModelZeroEvenWhenItIsEmpty()
    {
        // What the engine shows for an entity with no body value: the part's
        // first option, which here is the empty "off" model.
        IReadOnlyList<StudioTriangle> triangles = Read(
            StudioTestModel.Build(Quad, [0, 1, 2], leadingEmptyModel: true), StudioBodyChoice.Default);

        Assert.Empty(triangles);
    }

    [Fact]
    public void DefaultAndFirstDrawnAgreeWhenModelZeroHasTriangles()
    {
        (byte[] Mdl, byte[] Vvd, byte[] Vtx) files = StudioTestModel.Build(Quad, [0, 1, 2, 1, 3, 2]);

        Assert.Equal(Read(files, StudioBodyChoice.FirstDrawn), Read(files, StudioBodyChoice.Default));
    }

    [Fact]
    public void NullArgumentsThrow()
    {
        (byte[] mdl, byte[] vvd, byte[] vtx) = StudioTestModel.Build(Quad, [0, 1, 2]);
        MdlFile m = MdlFile.Parse(mdl);
        VvdFile v = VvdFile.Parse(vvd);
        VtxFile x = VtxFile.Parse(vtx);

        Assert.Throws<ArgumentNullException>(() => StudioMeshReader.Lod0Triangles(null!, v, x));
        Assert.Throws<ArgumentNullException>(() => StudioMeshReader.Lod0Triangles(m, null!, x));
        Assert.Throws<ArgumentNullException>(() => StudioMeshReader.Lod0Triangles(m, v, null!));
    }
}
