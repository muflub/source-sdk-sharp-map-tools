//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapGen.Content;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Options;
using SourceSharp.MapTools.Phys;
using SourceSharp.MapTools.Phys.Managed;
using SourceSharp.MapTools.Nav;
using SourceSharp.Tests.MapGen.Content;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>A prop's movement hull, read from its model's header in the game's content.</summary>
public sealed class NavModelBoundsTests
{
    /// <summary>A studio header's first bytes with a hull: enough for the navigation.</summary>
    internal static byte[] Mdl(Vec3 mins, Vec3 maxs, string ident = "IDST")
    {
        byte[] bytes = new byte[160];
        System.Text.Encoding.ASCII.GetBytes(ident).CopyTo(bytes, 0);
        float[] values = [mins.X, mins.Y, mins.Z, maxs.X, maxs.Y, maxs.Z];
        for (int i = 0; i < 6; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(104 + (i * 4)), values[i]);
        }

        return bytes;
    }

    [Fact]
    public void AHullIsReadFromTheHeaderAndABadOneIsNone()
    {
        Assert.Equal((new Vec3(-8, -4, 0), new Vec3(8, 4, 20)), NavModelBounds.Hull(Mdl(new Vec3(-8, -4, 0), new Vec3(8, 4, 20))));
        Assert.Null(NavModelBounds.Hull(Mdl(new Vec3(-8, -4, 0), new Vec3(8, 4, 20), "XXXX")));
        Assert.Null(NavModelBounds.Hull(Mdl(new Vec3(0, 0, 0), new Vec3(0, 4, 20))));
        Assert.Null(NavModelBounds.Hull(new byte[50]));
    }

    [Fact]
    public async Task HullsAreReadFromTheContentForTheModelsAMapsPropsName()
    {
        await using ContentFileSystem content = await StudioModelWriterTests.MountAsync(new Dictionary<string, byte[]>
        {
            ["models/crate.mdl"] = Mdl(new Vec3(-8, -4, 0), new Vec3(8, 4, 20)),
        });
        BspData bsp = new();
        List<BspEntity> entities = [new(), new(), new(), new(), new()];
        entities[0].Pairs.Add(new BspKeyValue("classname", "worldspawn"));
        entities[1].Pairs.Add(new BspKeyValue("classname", "prop_physics"));
        entities[1].Pairs.Add(new BspKeyValue("model", "models/crate.mdl"));
        entities[2].Pairs.Add(new BspKeyValue("classname", "prop_dynamic"));
        entities[2].Pairs.Add(new BspKeyValue("model", "models/gone.mdl"));
        entities[3].Pairs.Add(new BspKeyValue("classname", "prop_physics"));
        entities[3].Pairs.Add(new BspKeyValue("model", "models/crate.mdl"));
        entities[4].Pairs.Add(new BspKeyValue("classname", "func_door"));
        entities[4].Pairs.Add(new BspKeyValue("model", "*1"));
        bsp.SetLump(BspLump.Entities, EntityLump.Write(entities).Data);

        IReadOnlyList<string> models = NavModelBounds.PropModels(bsp);
        Assert.Equal(["models/crate.mdl", "models/gone.mdl"], models);
        IReadOnlyDictionary<string, (Vec3 Mins, Vec3 Maxs)?> hulls = await NavModelBounds.LoadAsync(content, [.. models, "not a path\0"]);
        Assert.Equal((new Vec3(-8, -4, 0), new Vec3(8, 4, 20)), hulls["models/crate.mdl"]);
        Assert.Null(hulls["models/gone.mdl"]);
        Assert.Null(hulls["not a path\0"]);
    }
    /// <summary>
    /// A cooked <c>.phy</c> of two posts with a gap between them: the
    /// collision pieces are the two posts' boxes, not one box over the gap.
    /// Bytes that are not a <c>.phy</c>, and a <c>.phy</c> whose solid is not
    /// a compact surface (the content generator's stand-in), give none.
    /// </summary>
    [Fact]
    public async Task APhysFilesPiecesAreItsConvexesBoxes()
    {
        await using ManagedCollisionCooker cooker = ManagedCollisionCooker.Create(ComplianceOptions.Correct);
        static Vec3[] Post(float x0, float x1) =>
        [
            new(x0, -4, 0), new(x1, -4, 0), new(x0, 4, 0), new(x1, 4, 0),
            new(x0, -4, 64), new(x1, -4, 64), new(x0, 4, 64), new(x1, 4, 64),
        ];

        byte[] collide = await cooker.RunAsync(session =>
        {
            ConvexHandle a = session.ConvexFromVerts(Post(-24, -16));
            ConvexHandle b = session.ConvexFromVerts(Post(16, 24));
            CollideHandle both = session.ConvertConvexToCollide([a, b]);
            try
            {
                return session.CollideWrite(both);
            }
            finally
            {
                session.DestroyCollide(both);
            }
        });

        byte[] header = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(header, 16);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 1);
        byte[] phy = [.. header, .. BitConverter.GetBytes(collide.Length), .. collide];

        IReadOnlyList<(Vec3 Mins, Vec3 Maxs)> pieces = NavModelBounds.Pieces(phy)!;
        Assert.Equal(2, pieces.Count);
        (Vec3 Mins, Vec3 Maxs)[] sorted = [.. pieces.OrderBy(p => p.Mins.X)];
        void Near(Vec3 expected, Vec3 actual)
        {
            Assert.InRange(actual.X, expected.X - 0.5f, expected.X + 0.5f);
            Assert.InRange(actual.Y, expected.Y - 0.5f, expected.Y + 0.5f);
            Assert.InRange(actual.Z, expected.Z - 0.5f, expected.Z + 0.5f);
        }

        Near(new Vec3(-24, -4, 0), sorted[0].Mins);
        Near(new Vec3(-16, 4, 64), sorted[0].Maxs);
        Near(new Vec3(16, -4, 0), sorted[1].Mins);
        Near(new Vec3(24, 4, 64), sorted[1].Maxs);

        Assert.Null(NavModelBounds.Pieces("not a phy"u8));
        Dictionary<string, byte[]> stand = new(StringComparer.Ordinal);
        foreach ((string path, byte[] bytes) in StudioModelWriter.Write(new StudioModelSpec
        {
            Name = "props_test/box.mdl",
            Materials = ["prop"],
            MaterialSearchPaths = ["models/props_test/"],
            Meshes = [Primitives.Box(0, new Vec3(-8, -8, 0), new Vec3(8, 8, 32))],
        }))
        {
            stand[path] = bytes;
        }

        Assert.Null(NavModelBounds.Pieces(stand["models/props_test/box.phy"]));
    }
}
