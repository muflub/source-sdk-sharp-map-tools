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
/// A one-cell hall compiled in the test for the facts about a player's
/// exact width: a platform 112 units up reached only by a solid ladder
/// brush 4 units off the west wall, and an east room reached only through
/// an opening 40 wide and 48 high in a dividing wall.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>The hall: floor top 16, ceiling 240, walls 16 thick round x, y 0..256.</description></item>
/// <item><description>The platform: x 16..128, y 16..112, top 128.</description></item>
/// <item><description>The ladder: a <c>%compileLadder</c> brush x 20..24 (4 off the wall's face at 16), y 128..160, up to 128.</description></item>
/// <item><description>The dividing wall: x 176..192 across the hall, open at y 100..140 from the floor to 64.</description></item>
/// </list>
/// </remarks>
public sealed class ClimbNavMap : IAsyncLifetime
{
    /// <summary>The ladder's material.</summary>
    public const string Ladder = "unit/ladder";

    /// <summary>A point on the hall's floor, west of the dividing wall.</summary>
    public static Vec3 Hall => new(96, 200, 17);

    /// <summary>A point on the platform.</summary>
    public static Vec3 Platform => new(72, 64, 129);

    /// <summary>A point in the east room.</summary>
    public static Vec3 East => new(216, 128, 17);

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
        void Solid(Vec3 mins, Vec3 maxs, string material = RoomHarness.Plain) => world.Children.Add(RoomModel.Slab(material, mins, maxs, id++));

        Solid(new Vec3(0, 0, 0), new Vec3(256, 256, 16));
        Solid(new Vec3(0, 0, 240), new Vec3(256, 256, 256));
        Solid(new Vec3(0, 0, 16), new Vec3(16, 256, 240));
        Solid(new Vec3(240, 0, 16), new Vec3(256, 256, 240));
        Solid(new Vec3(16, 0, 16), new Vec3(240, 16, 240));
        Solid(new Vec3(16, 240, 16), new Vec3(240, 256, 240));

        Solid(new Vec3(16, 16, 16), new Vec3(128, 112, 128));
        Solid(new Vec3(20, 128, 16), new Vec3(24, 160, 128), Ladder);

        Solid(new Vec3(176, 16, 16), new Vec3(192, 100, 240));
        Solid(new Vec3(176, 140, 16), new Vec3(192, 240, 240));
        Solid(new Vec3(176, 100, 64), new Vec3(192, 140, 240));

        VmfChunk start = new(MapFileLoader.EntityChunk);
        start.AddKey("id", "9000");
        start.AddKey("classname", "info_player_start");
        start.AddKey("origin", "96 200 40");
        document.Chunks.Add(start);
        return document;
    }

    /// <summary>Builds the map's navigation.</summary>
    public Nav3dReader Open(BspNavOptions? options = null) =>
        Nav3dReader.Open(Nav3dWriter.Write(LevelNavFromBsp.Build(Bsp, Nav3dBspBinding.Of(Bytes), null, options).Level));

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            [$"materials/{Ladder}.vmt"] = "\"LightmappedGeneric\"\n{\n\t\"$basetexture\" \"unit/missing\"\n\t\"%compileLadder\" \"1\"\n}\n"u8.ToArray(),
        };
        VbspContext context = await RoomHarness.ContextAsync(extraFiles: files);
        context.MapBase = "climb";
        VbspResult result = await RoomHarness.CompileAsync(Vmf(), context);
        Assert.NotNull(result.Bsp);
        using MemoryStream stream = new();
        await BspFile.SaveAsync(result.Bsp!, stream, CancellationToken.None);
        Bytes = stream.ToArray();
        Bsp = await BspFile.LoadAsync(new MemoryStream(Bytes, writable: false));
    }

    /// <inheritdoc/>
    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// A player's exact width: fitting at a voxel's centre answers a 32-wide
/// player in 32 units, not the 48 a fit anywhere in a 16-unit voxel needs;
/// a ladder close to a wall is a climb the player takes; and the walking
/// components join what a player standing, crouching and climbing reaches.
/// </summary>
public sealed class LevelNavFromBspClimbTests(ClimbNavMap map) : IClassFixture<ClimbNavMap>
{
    private static int Stand(Nav3dReader nav, Vec3 at)
    {
        int leaf = nav.FindLeaf(at);
        Assert.True(leaf >= 0, $"no leaf at {at}");
        return leaf;
    }

    /// <summary>
    /// The ladder is a solid brush 4 off the wall; the voxels a climbing
    /// player's origin is in (a half-width out from it) are ladder leaves,
    /// and the player fits at the centre of those beside the ladder's face.
    /// </summary>
    [Fact]
    public void ALadderCloseToAWallHasLadderLeavesThePlayerFitsIn()
    {
        Nav3dReader nav = map.Open();
        int climb = Stand(nav, new Vec3(40, 136, 64));
        Assert.NotEqual(Nav3dLeafFlags.None, nav.Leaf(climb).Flags & Nav3dLeafFlags.Ladder);
        Assert.True(nav.PassableAtCentre(climb, nav.Leaf(climb).ZLo, 32, 36, Nav3dClipClass.Player));
        Assert.False(nav.Passable(climb, nav.Leaf(climb).ZLo, 32, 36, Nav3dClipClass.Player));
        Assert.True(Nav3dWalk.IsClimb(nav, Nav3dWalker.Player, climb));
        Assert.Equal(-1, nav.FindLeaf(new Vec3(22, 140, 64)));
    }

    /// <summary>
    /// A player reaches the platform only by the ladder, the east room only
    /// crouched through the 48-high opening; fitted at the voxel's centre
    /// all three are one walking component. Fitted anywhere in the voxel,
    /// neither the climb beside the ladder nor the 40-wide opening fits, and
    /// standing alone (no crouch) the opening is closed.
    /// </summary>
    [Fact]
    public void ThePlayerWalksCrouchesAndClimbsToEveryPartOfTheMap()
    {
        Nav3dReader nav = map.Open();
        int hall = Stand(nav, ClimbNavMap.Hall);
        int platform = Stand(nav, ClimbNavMap.Platform);
        int east = Stand(nav, ClimbNavMap.East);

        (int[] player, _) = Nav3dWalk.Components(nav, Nav3dWalker.Player);
        Assert.True(player[hall] >= 0);
        Assert.Equal(player[hall], player[platform]);
        Assert.Equal(player[hall], player[east]);

        (int[] anywhere, _) = Nav3dWalk.Components(nav, Nav3dWalker.Player with { Fit = Nav3dFit.Anywhere });
        Assert.NotEqual(anywhere[hall], anywhere[platform]);
        Assert.NotEqual(anywhere[hall], anywhere[east]);

        (int[] standing, _) = Nav3dWalk.Components(nav, Nav3dWalker.Player with { CrouchHeight = 72 });
        Assert.Equal(standing[hall], standing[platform]);
        Assert.NotEqual(standing[hall], standing[east]);
    }

    /// <summary>
    /// The opening is 40 wide: a 32-wide player fits at the centre of the
    /// voxel in the middle of it, not anywhere in that voxel; and 48 high
    /// from the floor: crouched (36) it fits from its feet, standing (72)
    /// it does not.
    /// </summary>
    [Fact]
    public void FittingAtTheCentreAnswersThePlayersExactWidthAndHeight()
    {
        Nav3dReader nav = map.Open();
        int gap = Stand(nav, new Vec3(184, 120, 17));
        Assert.True(nav.StandableAtCentre(gap, 32, 36, Nav3dClipClass.Player));
        Assert.False(nav.StandableAtCentre(gap, 32, 72, Nav3dClipClass.Player));
        Assert.False(nav.Standable(gap, nav.Leaf(gap).ZLo, 32, 36, Nav3dClipClass.Player));
        Assert.False(nav.StandableAtCentre(gap, 48, 36, Nav3dClipClass.Player));
        Assert.True(nav.FitsAtCentre(gap, nav.Leaf(gap).ZLo, 32, 47, 16, Nav3dClipClass.Player));
        Assert.False(nav.FitsAtCentre(gap, nav.Leaf(gap).ZLo, 32, 49, 16, Nav3dClipClass.Player));
    }

    /// <summary>The same map gives the same bytes at one thread and at four.</summary>
    [Fact]
    public void TheClimbMapIsTheSameBytesAtAnyThreadCount()
    {
        byte[] one = Nav3dWriter.Write(LevelNavFromBsp.Build(map.Bsp, Nav3dBspBinding.Of(map.Bytes), null).Level);
        byte[] four = Nav3dWriter.Write(LevelNavFromBsp.Build(map.Bsp, Nav3dBspBinding.Of(map.Bytes), null, new BspNavOptions { MaxDegree = 4 }).Level);
        Assert.Equal(one, four);
    }
}
