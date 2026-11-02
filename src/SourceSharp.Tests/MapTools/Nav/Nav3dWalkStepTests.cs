//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

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
/// A one-cell hall split by a sheer step 18 high: the floor's top is 16, and
/// a platform over the east half (x 128..240, wall to wall) stands at 34.
/// The step's face is on a voxel boundary, and the platform is reached only
/// over it.
/// </summary>
public sealed class StepNavMap : IAsyncLifetime
{
    /// <summary>A point on the floor, west of the step.</summary>
    public static Vec3 Low => new(64, 128, 17);

    /// <summary>A point on the platform.</summary>
    public static Vec3 High => new(184, 128, 50);

    /// <summary>The compiled map's bytes.</summary>
    public byte[] Bytes { get; private set; } = [];

    /// <summary>The map as read back.</summary>
    public BspData Bsp { get; private set; } = null!;

    /// <summary>The map's VMF.</summary>
    public static VmfDocument Vmf()
    {
        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");
        document.Chunks.Add(world);
        int id = 100;
        void Solid(Vec3 mins, Vec3 maxs) => world.Children.Add(RoomModel.Slab(RoomHarness.Plain, mins, maxs, id++));

        Solid(new Vec3(0, 0, 0), new Vec3(256, 256, 16));
        Solid(new Vec3(0, 0, 240), new Vec3(256, 256, 256));
        Solid(new Vec3(0, 0, 16), new Vec3(16, 256, 240));
        Solid(new Vec3(240, 0, 16), new Vec3(256, 256, 240));
        Solid(new Vec3(16, 0, 16), new Vec3(240, 16, 240));
        Solid(new Vec3(16, 240, 16), new Vec3(240, 256, 240));
        Solid(new Vec3(128, 16, 16), new Vec3(240, 240, 34));

        VmfChunk start = new(MapFileLoader.EntityChunk);
        start.AddKey("id", "9000");
        start.AddKey("classname", "info_player_start");
        start.AddKey("origin", "64 128 40");
        document.Chunks.Add(start);
        return document;
    }

    /// <summary>Builds the map's navigation.</summary>
    public Nav3dReader Open(BspNavOptions? options = null) =>
        Nav3dReader.Open(Nav3dWriter.Write(LevelNavFromBsp.Build(Bsp, Nav3dBspBinding.Of(Bytes), null, options).Level));

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        Bytes = await BspNavMap.CompileAsync(Vmf());
        Bsp = await BspFile.LoadAsync(new MemoryStream(Bytes, writable: false));
    }

    /// <inheritdoc/>
    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// The reference walker climbs a sheer step as a player does: up to the
/// step height, from the floor in front of the step to the top beside it,
/// even though no voxel at the step's foot holds the player's box.
/// </summary>
public sealed class Nav3dWalkStepTests(StepNavMap map) : IClassFixture<StepNavMap>
{
    private static int LeafAt(Nav3dReader nav, Vec3 at)
    {
        int leaf = nav.FindLeaf(at);
        Assert.True(leaf >= 0, $"no leaf at {at}");
        return leaf;
    }

    /// <summary>
    /// The step's foot (the column x 112..128) is no place to stand for a
    /// 32-wide player at its centre: the step is 8 units from the centre.
    /// The player stands two columns out (x 96..112) and on the step's top
    /// (x 128..144); its box crosses the foot at the top's height, so the two
    /// are one walking component, standing or crouched, at both fits.
    /// </summary>
    [Fact]
    public void ASheerStepOfTheStepHeightIsClimbed()
    {
        Nav3dReader nav = map.Open();
        int low = LeafAt(nav, StepNavMap.Low);
        int high = LeafAt(nav, StepNavMap.High);
        Assert.Equal((16f, 34f), (nav.Leaf(low).FloorZ(Nav3dClipClass.Player), nav.Leaf(high).FloorZ(Nav3dClipClass.Player)));
        Assert.False(Nav3dWalk.IsStand(nav, Nav3dWalker.Player, LeafAt(nav, new Vec3(120, 128, 17))));
        Assert.True(Nav3dWalk.IsStand(nav, Nav3dWalker.Player, LeafAt(nav, new Vec3(104, 128, 17))));
        Assert.True(Nav3dWalk.IsStand(nav, Nav3dWalker.Player, LeafAt(nav, new Vec3(136, 128, 50))));
        Assert.Empty(Enumerable.Range(0, nav.JumpCount).Select(nav.Jump));

        (int[] player, _) = Nav3dWalk.Components(nav, Nav3dWalker.Player);
        Assert.True(player[low] >= 0);
        Assert.Equal(player[low], player[high]);

        (int[] standing, _) = Nav3dWalk.Components(nav, Nav3dWalker.Player with { CrouchHeight = 72 });
        Assert.Equal(standing[low], standing[high]);
    }

    /// <summary>
    /// With a step height of 17 the same step is one unit too high: the
    /// platform is not walked to (and no jump link joins it: the step's
    /// foot is no floor the player stands on), so the move is the step
    /// height's and nothing else's.
    /// </summary>
    [Fact]
    public void AStepOverTheStepHeightIsNotClimbed()
    {
        Nav3dReader nav = map.Open(new BspNavOptions { Settings = NavSettings.Default with { StepHeight = 17 } });
        int low = LeafAt(nav, StepNavMap.Low);
        int high = LeafAt(nav, StepNavMap.High);

        (int[] player, _) = Nav3dWalk.Components(nav, Nav3dWalker.Player);
        Assert.True(player[low] >= 0 && player[high] >= 0);
        Assert.NotEqual(player[low], player[high]);
    }

    /// <summary>
    /// A beam across the hall over the step's foot (x 108..118, from 54 up)
    /// leaves the player room to crouch in front of the step (feet 16, head
    /// 52) and on its top beside it (x 128 on), but not to cross the foot at
    /// the top's height (feet 34, head 70): the move needs the column between
    /// to hold the box there, so the platform is not reached.
    /// </summary>
    [Fact]
    public async Task AStepWhoseFootTheBoxCannotCrossIsNotClimbed()
    {
        VmfDocument vmf = StepNavMap.Vmf();
        VmfChunk world = vmf.Chunks.Single(c => c.Name == MapFileLoader.WorldChunk);
        world.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(108, 16, 54), new Vec3(118, 240, 240), 999));
        byte[] bytes = await BspNavMap.CompileAsync(vmf);
        BspData bsp = await BspFile.LoadAsync(new MemoryStream(bytes, writable: false));
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(LevelNavFromBsp.Build(bsp, Nav3dBspBinding.Of(bytes), null).Level));

        (int[] player, _) = Nav3dWalk.Components(nav, Nav3dWalker.Player);
        int low = LeafAt(nav, StepNavMap.Low);
        int high = LeafAt(nav, StepNavMap.High);
        Assert.True(player[low] >= 0 && player[high] >= 0);
        Assert.NotEqual(player[low], player[high]);
        Assert.True(Nav3dWalk.IsStand(nav, Nav3dWalker.Player, LeafAt(nav, new Vec3(104, 128, 17))));
        Assert.True(Nav3dWalk.IsStand(nav, Nav3dWalker.Player, LeafAt(nav, new Vec3(136, 128, 50))));
    }
}
