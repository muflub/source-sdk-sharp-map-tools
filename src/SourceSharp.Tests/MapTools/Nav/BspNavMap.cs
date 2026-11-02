//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// A small ordinary map, not made of rooms, compiled in the test for the
/// navigation-from-a-compiled-map facts: an L of a 512 x 256 hall and a
/// corridor running north from its west end, so a bounding box of 2 x 3
/// cells holds two cells that are outside the map throughout.
/// </summary>
/// <remarks>
/// <para>In the hall (floor top 16, ceiling 240):</para>
/// <list type="bullet">
/// <item><description>a step, 16 high, at (32..96, 32..96);</description></item>
/// <item><description>a ledge, 40 high, at (160..240, 32..224): over the step, under a crouch jump;</description></item>
/// <item><description>a player clip slab at (300..364, 32..224), up to 128;</description></item>
/// <item><description>a <c>func_door</c> "gate" at (400..416, 16..240), full height;</description></item>
/// <item><description>a <c>prop_physics</c> "crate" (the 16 x 16 x 32 box) at (448, 128);</description></item>
/// <item><description>a <c>prop_static</c> box at (64, 176): solid 56..72, 168..184, 16..48;</description></item>
/// <item><description>an <c>info_ladder</c> at (450..480, 20..40, 16..200).</description></item>
/// </list>
/// <para>
/// In the corridor (x 16..112): a displacement patch at y 400..496 whose
/// surface stands at 32, and a <c>func_useableladder</c> at (64, 600).
/// </para>
/// </remarks>
public sealed class BspNavMap : IAsyncLifetime
{
    /// <summary>The hall's floor top.</summary>
    public const float Floor = 16;

    /// <summary>The step's top.</summary>
    public const float StepTop = 32;

    /// <summary>The ledge's top.</summary>
    public const float LedgeTop = 56;

    /// <summary>The displacement's surface.</summary>
    public const float PatchTop = 32;

    /// <summary>The compiled map's bytes.</summary>
    public byte[] Bytes { get; private set; } = [];

    /// <summary>The map as read back from <see cref="Bytes"/>.</summary>
    public BspData Bsp { get; private set; } = null!;

    /// <summary>The prop models' hulls, from the harness models' headers.</summary>
    public static (Vec3 Mins, Vec3 Maxs)? Hull(string model) =>
        RoomPropHarness.Models().TryGetValue(model, out byte[]? bytes) ? NavModelBounds.Hull(bytes) : null;

    /// <summary>The map's VMF.</summary>
    public static VmfDocument Vmf()
    {
        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");
        document.Chunks.Add(world);
        int id = 100;
        void Solid(Vec3 mins, Vec3 maxs, string material = RoomHarness.Plain) => world.Children.Add(RoomModel.Slab(material, mins, maxs, id++));

        // The shell: floors, ceilings and walls of the hall and the corridor.
        Solid(new Vec3(0, 0, 0), new Vec3(512, 256, 16));
        Solid(new Vec3(0, 256, 0), new Vec3(128, 768, 16));
        Solid(new Vec3(0, 0, 240), new Vec3(512, 256, 256));
        Solid(new Vec3(0, 256, 240), new Vec3(128, 768, 256));
        Solid(new Vec3(0, 0, 16), new Vec3(16, 768, 240));
        Solid(new Vec3(496, 0, 16), new Vec3(512, 256, 240));
        Solid(new Vec3(16, 0, 16), new Vec3(496, 16, 240));
        Solid(new Vec3(128, 240, 16), new Vec3(496, 256, 240));
        Solid(new Vec3(112, 256, 16), new Vec3(128, 768, 240));
        Solid(new Vec3(16, 752, 16), new Vec3(112, 768, 240));

        // The hall's features.
        Solid(new Vec3(32, 32, 16), new Vec3(96, 96, 32));
        Solid(new Vec3(160, 32, 16), new Vec3(240, 224, 56));
        Solid(new Vec3(300, 32, 16), new Vec3(364, 224, 128), RoomHarness.PlayerClip);

        // The corridor's displacement: a patch whose surface is 8 over its 24-high brush.
        world.Children.Add(RoomDisplacementHarness.Patch(
            RoomDisplacementHarness.PatchBrush, new Box(new Vec3(16, 400, 16), new Vec3(112, 496, 24)), power: 2, offsets: false,
            heights: (_, _) => 8));

        VmfChunk door = Entity("func_door", 9001, ("targetname", "gate"));
        door.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(400, 16, 16), new Vec3(416, 240, 240), 9002));
        document.Chunks.Add(door);
        document.Chunks.Add(Entity("prop_physics", 9003, ("targetname", "crate"), ("model", RoomPropHarness.BoxModel), ("origin", "448 128 16")));
        document.Chunks.Add(RoomPropHarness.Prop(9004, RoomPropHarness.BoxModel, new Vec3(64, 176, 16), "0 0 0", ("solid", "6")));
        document.Chunks.Add(Entity("info_ladder", 9005, ("mins", "450 20 16"), ("maxs", "480 40 200")));
        document.Chunks.Add(Entity("func_useableladder", 9006, ("point0", "64 600 16"), ("point1", "64 600 200")));
        document.Chunks.Add(Entity("info_player_start", 9007, ("origin", "256 128 40")));
        return document;
    }

    private static VmfChunk Entity(string className, int id, params (string Key, string Value)[] keys)
    {
        VmfChunk entity = new(MapFileLoader.EntityChunk);
        entity.AddKey("id", id.ToString(CultureInfo.InvariantCulture));
        entity.AddKey("classname", className);
        foreach ((string key, string value) in keys)
        {
            entity.AddKey(key, value);
        }

        return entity;
    }

    /// <summary>Compiles a document as vbsp would and returns the written map's bytes.</summary>
    public static async Task<byte[]> CompileAsync(VmfDocument document)
    {
        VbspResult result = await RoomHarness.CompileAsync(document, await RoomPropHarness.ContextAsync("navmap"));
        Assert.NotNull(result.Bsp);
        using MemoryStream stream = new();
        await BspFile.SaveAsync(result.Bsp!, stream, CancellationToken.None);
        return stream.ToArray();
    }

    /// <summary>Builds the map's navigation.</summary>
    public BspNavResult Build(BspNavOptions? options = null, bool props = true) =>
        LevelNavFromBsp.Build(Bsp, Nav3dBspBinding.Of(Bytes), props ? Hull : null, options);

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        Bytes = await CompileAsync(Vmf());
        Bsp = await BspFile.LoadAsync(new MemoryStream(Bytes, writable: false));
    }

    /// <inheritdoc/>
    public Task DisposeAsync() => Task.CompletedTask;
}
