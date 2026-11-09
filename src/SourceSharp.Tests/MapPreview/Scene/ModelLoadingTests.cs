//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapPreview.Scene;
using SourceSharp.MapTools.Io;
using SourceSharp.Tests.AssetDb;
using Xunit;

namespace SourceSharp.Tests.MapPreview.Scene;

/// <summary>
/// Facts for <see cref="PreviewModelLoader"/>, rest-pose skinning in
/// <see cref="StudioMeshReader"/>, and <see cref="LayeredContent"/>.
/// </summary>
public class ModelLoadingTests
{
    private static readonly Vec3[] Quad = [new(0, 0, 0), new(10, 0, 0), new(0, 10, 0), new(10, 10, 0)];

    private static StudioBone Identity()
    {
        StudioBone bone = new() { Pos = Vec3.Zero };
        bone.Quat[3] = 1;
        bone.PoseToBone[0] = 1;
        bone.PoseToBone[5] = 1;
        bone.PoseToBone[10] = 1;
        return bone;
    }

    private static async Task<ContentFileSystem> GameAsync(bool vvd = true, bool vtx = true, bool material = true, bool corrupt = false, StudioBone? bone = null)
    {
        (byte[] mdl, byte[] v, byte[] x) = StudioTestModel.Build(Quad, [0, 1, 2], bone: bone ?? Identity());
        List<(string, byte[])> files = [("models/gadget.mdl", corrupt ? [1, 2, 3] : mdl)];
        if (vvd)
        {
            files.Add(("models/gadget.vvd", v));
        }

        if (vtx)
        {
            files.Add(("models/gadget.dx80.vtx", x));
        }

        if (material)
        {
            files.Add(("materials/models/gadget/body.vmt", "\"VertexLitGeneric\"\n{\n}\n"u8.ToArray()));
        }

        return await PreviewTestMaps.ContentAsync([.. files]);
    }

    [Fact]
    public async Task AModelLoadsWithItsResolvedMaterialAndHull()
    {
        await using ContentFileSystem game = await GameAsync();

        (IReadOnlyDictionary<string, PreviewModel> models, IReadOnlyList<ModelLoadIssue> issues) =
            await PreviewModelLoader.LoadAsync(["models/gadget.mdl", "MODELS/GADGET.MDL"], game);

        Assert.Empty(issues);
        PreviewModel model = Assert.Single(models.Values);
        Assert.Single(model.Triangles);
        Assert.Equal("models/gadget/body", model.MaterialFor(0, 0)); // no search path has it; the direct name does
        Assert.Equal(new Vec3(-64, -64, 0), model.HullMin);
    }

    [Theory]
    [InlineData(false, true, false, "no .vvd")]
    [InlineData(true, false, false, "no .vtx")]
    [InlineData(true, true, true, "not a readable model")]
    public async Task AModelMissingAPieceIsReportedNotThrown(bool vvd, bool vtx, bool corrupt, string reason)
    {
        await using ContentFileSystem game = await GameAsync(vvd, vtx, corrupt: corrupt);

        (IReadOnlyDictionary<string, PreviewModel> models, IReadOnlyList<ModelLoadIssue> issues) =
            await PreviewModelLoader.LoadAsync(["models/gadget.mdl"], game);

        Assert.Empty(models);
        Assert.Contains(reason, Assert.Single(issues).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAbsentModelIsReported()
    {
        await using ContentFileSystem game = await GameAsync();

        (_, IReadOnlyList<ModelLoadIssue> issues) = await PreviewModelLoader.LoadAsync(["models/nothing.mdl"], game);

        Assert.Equal("models/nothing.mdl", Assert.Single(issues).Model);
    }

    [Fact]
    public async Task AnUnresolvedMaterialKeepsItsFirstCandidateName()
    {
        await using ContentFileSystem game = await GameAsync(material: false);

        (IReadOnlyDictionary<string, PreviewModel> models, _) = await PreviewModelLoader.LoadAsync(["models/gadget.mdl"], game);

        Assert.Equal("models/gadgets/models/gadget/body", models["models/gadget.mdl"].MaterialFor(0, 0));
    }

    [Fact]
    public async Task LoadingIsCancellable()
    {
        await using ContentFileSystem game = await GameAsync();
        using CancellationTokenSource cancel = new();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PreviewModelLoader.LoadAsync(["models/gadget.mdl"], game, cancel.Token));
    }

    [Fact]
    public void AnIdentityRestPoseLeavesVerticesWhereTheyAre()
    {
        (byte[] mdl, byte[] vvd, byte[] vtx) = StudioTestModel.Build(Quad, [0, 1, 2], bone: Identity());
        MdlFile m = MdlFile.Parse(mdl);

        IReadOnlyList<StudioTriangle> plain = StudioMeshReader.Lod0Triangles(m, VvdFile.Parse(vvd), VtxFile.Parse(vtx));
        IReadOnlyList<StudioTriangle> posed = StudioMeshReader.Lod0Triangles(m, VvdFile.Parse(vvd), VtxFile.Parse(vtx), restPose: true);

        Assert.Equal(plain, posed);
    }

    [Fact]
    public void ARestPoseMovesVerticesThroughTheBone()
    {
        // A bone translated by (0, 0, 50) with an identity pose-to-bone: the
        // rest pose lifts the mesh 50 units.
        StudioBone bone = Identity();
        bone.Pos = new Vec3(0, 0, 50);
        (byte[] mdl, byte[] vvd, byte[] vtx) = StudioTestModel.Build(Quad, [0, 1, 2], bone: bone);

        StudioTriangle t = Assert.Single(StudioMeshReader.Lod0Triangles(MdlFile.Parse(mdl), VvdFile.Parse(vvd), VtxFile.Parse(vtx), restPose: true));

        Assert.Equal(new Vec3(0, 0, 50), t.A);
        Assert.Equal(new Vec3(10, 0, 50), t.B);
    }

    [Fact]
    public void QuaternionsAndComposedMatricesFollowTheConvention()
    {
        Span<float> m = stackalloc float[12];
        float h = MathF.Sqrt(0.5f);
        StudioMeshReader.QuaternionMatrix(0, 0, h, h, new Vec3(1, 2, 3), m); // 90 degrees about Z
        Assert.Equal(0f, m[0], 5);
        Assert.Equal(-1f, m[1], 5);
        Assert.Equal(1f, m[4], 5);
        Assert.Equal(3f, m[11]);

        Span<float> twice = stackalloc float[12];
        StudioMeshReader.Concat(m, m, twice);
        Assert.Equal(-1f, twice[0], 5); // 180 degrees
        Assert.Equal(1f - 2f, twice[3], 5); // R*t + t: (-2, 1, 3) + (1, 2, 3)
    }

    [Fact]
    public void ALyingCharacterIsStoodUpAndAPropIsNot()
    {
        (byte[] mdl, _, _) = StudioTestModel.Build(Quad, [0, 1, 2], flags: 0, bone: Identity());
        MdlFile character = MdlFile.Parse(mdl);
        (byte[] prop, _, _) = StudioTestModel.Build(Quad, [0, 1, 2], flags: 0x10, bone: Identity());

        // Tall along Y (60, matching the 64-high hull), flat along Z.
        StudioTriangle lying = new(new Vec3(0, 0, 0), new Vec3(10, 60, 0), new Vec3(0, 30, 2), new Vec3(0, 1, 0), new Vec3(0, 1, 0), new Vec3(0, 1, 0), (0, 0), (0, 0), (0, 0), 0);

        StudioTriangle stood = Assert.Single(PreviewModelLoader.StandUp([lying], character));
        Assert.Equal(new Vec3(10, 0, 60), stood.B);
        Assert.Equal(new Vec3(0, 0, 1), stood.NormalA);
        Assert.Equal(lying, Assert.Single(PreviewModelLoader.StandUp([lying], MdlFile.Parse(prop))));
    }

    [Fact]
    public void AnUprightOrEmptyModelIsLeftAlone()
    {
        (byte[] mdl, _, _) = StudioTestModel.Build(Quad, [0, 1, 2], flags: 0, bone: Identity());
        MdlFile character = MdlFile.Parse(mdl);
        StudioTriangle upright = new(new Vec3(0, 0, 0), new Vec3(10, 0, 60), new Vec3(0, 5, 30), Vec3.Zero, Vec3.Zero, Vec3.Zero, (0, 0), (0, 0), (0, 0), 0);

        Assert.Equal(upright, Assert.Single(PreviewModelLoader.StandUp([upright], character)));
        Assert.Empty(PreviewModelLoader.StandUp([], character));
    }

    [Fact]
    public async Task TheFrontLayerShadowsTheBack()
    {
        await using ContentFileSystem front = await PreviewTestMaps.ContentAsync(("a.txt", [1]));
        await using ContentFileSystem back = await PreviewTestMaps.ContentAsync(("a.txt", [2]), ("b.txt", [3]));
        LayeredContent layered = new(front, back);

        using IMemoryOwner<byte>? a = await layered.ReadAsync(VPath.Create("a.txt"));
        using IMemoryOwner<byte>? b = await layered.ReadAsync(VPath.Create("b.txt"));
        Assert.Equal(1, a!.Memory.Span[0]);
        Assert.Equal(3, b!.Memory.Span[0]);
        Assert.NotNull(await layered.ResolveAsync(VPath.Create("b.txt")));
        Assert.Null(await layered.ResolveAsync(VPath.Create("c.txt")));
        Assert.NotNull(await layered.ReadRangeAsync(VPath.Create("b.txt"), 0, 1));

        List<VPath> listed = [];
        await foreach (VPath path in layered.EnumerateAsync(VPath.Empty, "*.txt"))
        {
            listed.Add(path);
        }

        Assert.Equal(2, listed.Count);
        Assert.Throws<ArgumentNullException>(() => new LayeredContent(null!, back));
        Assert.Throws<ArgumentNullException>(() => new LayeredContent(front, null!));
    }
}
