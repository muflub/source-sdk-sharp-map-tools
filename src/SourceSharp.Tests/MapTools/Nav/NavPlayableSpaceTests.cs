//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Bsp;
using SourceSharp.MapCompile;
using SourceSharp.MapTools.Bsp.Driver;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Nav;
using SourceSharp.MapTools.Rooms;
using SourceSharp.MapTools.Vis;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// A map laid out as a shipped TF map is: a play room with the spawns, a 3D
/// skybox room with the <c>sky_camera</c> far above it, and an end-of-match
/// summary stage with its own spawns far below it, each sealed on its own,
/// compiled through vbsp and vvis. The world is 5,888 units tall, 368 voxels
/// of 16, more than a column holds.
/// </summary>
public sealed class TallSkyboxMap : IAsyncLifetime
{
    /// <summary>The play room's inside: 512 x 512, 256 high, floor at 0.</summary>
    public static readonly Box Play = new(new Vec3(0, 0, 0), new Vec3(512, 512, 256));

    /// <summary>The compiled map's bytes, with vis.</summary>
    public byte[] Bytes { get; private set; } = [];

    /// <summary>The map as read back from <see cref="Bytes"/>.</summary>
    public BspData Bsp { get; private set; } = null!;

    /// <summary>The map's VMF: three sealed rooms stacked far apart.</summary>
    /// <param name="summarySpawn">Whether the low room holds a match-summary spawn (else it holds a plain one).</param>
    public static VmfDocument Vmf(bool summarySpawn = true)
    {
        VmfDocument document = Rooms(
            [(Play.Mins, Play.Maxs), (new Vec3(0, 0, 3000), new Vec3(256, 256, 3256)), (new Vec3(0, 0, -2600), new Vec3(256, 256, -2400))]);
        document.Chunks.Add(Entity("info_player_teamspawn", 9001, ("origin", "128 128 0"), ("TeamNum", "2")));
        document.Chunks.Add(Entity("info_player_teamspawn", 9002, ("origin", "384 384 0"), ("TeamNum", "3")));
        document.Chunks.Add(Entity("sky_camera", 9003, ("origin", "128 128 3100"), ("scale", "16")));
        document.Chunks.Add(summarySpawn
            ? Entity("info_player_teamspawn", 9004, ("origin", "128 128 -2600"), ("TeamNum", "0"), ("MatchSummary", "1"))
            : Entity("info_player_teamspawn", 9004, ("origin", "128 128 -2600"), ("TeamNum", "0")));
        return document;
    }

    /// <summary>A world of sealed hollow boxes, walls 16 thick, round each inside given.</summary>
    public static VmfDocument Rooms(IReadOnlyList<(Vec3 Lo, Vec3 Hi)> insides)
    {
        VmfDocument document = new();
        VmfChunk world = new(MapFileLoader.WorldChunk);
        world.AddKey("id", "1");
        world.AddKey("classname", "worldspawn");
        document.Chunks.Add(world);
        int id = 100;

        // A hollow box, walls 16 thick, around the inside given.
        void Room(Vec3 lo, Vec3 hi)
        {
            const float T = 16;
            world.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(lo.X - T, lo.Y - T, lo.Z - T), new Vec3(hi.X + T, hi.Y + T, lo.Z), id++));
            world.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(lo.X - T, lo.Y - T, hi.Z), new Vec3(hi.X + T, hi.Y + T, hi.Z + T), id++));
            world.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(lo.X - T, lo.Y - T, lo.Z), new Vec3(lo.X, hi.Y + T, hi.Z), id++));
            world.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(hi.X, lo.Y - T, lo.Z), new Vec3(hi.X + T, hi.Y + T, hi.Z), id++));
            world.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(lo.X, lo.Y - T, lo.Z), new Vec3(hi.X, lo.Y, hi.Z), id++));
            world.Children.Add(RoomModel.Slab(RoomHarness.Plain, new Vec3(lo.X, hi.Y, lo.Z), new Vec3(hi.X, hi.Y + T, hi.Z), id++));
        }

        foreach ((Vec3 lo, Vec3 hi) in insides)
        {
            Room(lo, hi);
        }

        return document;
    }

    /// <summary>A point entity with its keys.</summary>
    public static VmfChunk Entity(string className, int id, params (string Key, string Value)[] keys)
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

    /// <summary>Compiles a document through vbsp and vvis and reads the map back.</summary>
    public static async Task<(byte[] Bytes, BspData Bsp)> CompileAsync(VmfDocument document)
    {
        VbspResult result = await RoomHarness.CompileAsync(document, await RoomHarness.ContextAsync());
        Assert.NotNull(result.Bsp);
        await RoomHarness.VisAsync(result.Bsp!, PortalSet.FromPortalFile(result.Portals!));
        using MemoryStream stream = new();
        await BspFile.SaveAsync(result.Bsp!, stream, CancellationToken.None);
        byte[] bytes = stream.ToArray();
        return (bytes, await BspFile.LoadAsync(new MemoryStream(bytes, writable: false)));
    }

    /// <inheritdoc/>
    public async Task InitializeAsync() => (Bytes, Bsp) = await CompileAsync(Vmf());

    /// <inheritdoc/>
    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// Two play rooms side by side, each with a team's spawn and that team's flag,
/// and a sealed 3D skybox room between them, inside the play rooms' bounds, so
/// only the leaf cull can leave it out; compiled through vbsp and vvis.
/// </summary>
public sealed class SkyboxBetweenMap : IAsyncLifetime
{
    /// <summary>The skybox room's inside.</summary>
    public static readonly Box Sky = new(new Vec3(640, 128, 0), new Vec3(896, 384, 192));

    /// <summary>Where the game would send a player: the spawns and the flags, all on the play rooms' floors.</summary>
    public static readonly (string Class, Vec3 At)[] Targets =
    [
        ("info_player_teamspawn", new Vec3(128, 256, 0)),
        ("item_teamflag", new Vec3(384, 256, 0)),
        ("info_player_teamspawn", new Vec3(1408, 256, 0)),
        ("item_teamflag", new Vec3(1152, 256, 0)),
    ];

    public byte[] Bytes { get; private set; } = [];

    public BspData Bsp { get; private set; } = null!;

    public static VmfDocument Vmf()
    {
        VmfDocument document = TallSkyboxMap.Rooms(
            [(new Vec3(0, 0, 0), new Vec3(512, 512, 256)), (new Vec3(1024, 0, 0), new Vec3(1536, 512, 256)), (Sky.Mins, Sky.Maxs)]);
        int id = 9100;
        foreach ((string cls, Vec3 at) in Targets)
        {
            document.Chunks.Add(TallSkyboxMap.Entity(cls, id++, ("origin", RoomModel.Tuple(at.X, at.Y, at.Z)), ("TeamNum", at.X < 700 ? "2" : "3")));
        }

        document.Chunks.Add(TallSkyboxMap.Entity("sky_camera", id, ("origin", "768 256 64"), ("scale", "16")));
        return document;
    }

    public async Task InitializeAsync() => (Bytes, Bsp) = await TallSkyboxMap.CompileAsync(Vmf());

    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// The play space's leaf cull: a skybox inside the grid is never voxelised,
/// every place the game sends a player keeps its floor, and <c>PlaySpace = false</c>
/// builds the whole world as before.
/// </summary>
public sealed class NavPlaySpaceCullTests(SkyboxBetweenMap map) : IClassFixture<SkyboxBetweenMap>
{
    private Nav3dReader Build(bool playSpace) =>
        Nav3dReader.Open(Nav3dWriter.Write(LevelNavFromBsp.Build(map.Bsp, Nav3dBspBinding.Of(map.Bytes), null, new BspNavOptions { PlaySpace = playSpace }).Level));

    private static Vec3 SkyMiddle => new((SkyboxBetweenMap.Sky.Mins.X + SkyboxBetweenMap.Sky.Maxs.X) / 2, 256, 40);

    [Fact]
    public void WithoutTheCullTheSkyboxIsVoxelised() => Assert.True(Build(playSpace: false).FindLeaf(SkyMiddle) >= 0);

    [Fact]
    public void TheCullLeavesTheSkyboxOut() => Assert.Equal(-1, Build(playSpace: true).FindLeaf(SkyMiddle));

    [Fact]
    public void TheCullBuildsFewerLeaves() => Assert.True(Build(playSpace: true).LeafCount < Build(playSpace: false).LeafCount);

    [Fact]
    public void EveryPlaceTheGameSendsAPlayerKeepsAFloorAPlayerStandsOn()
    {
        Nav3dReader nav = Build(playSpace: true);
        Assert.All(SkyboxBetweenMap.Targets, t =>
        {
            int leaf = nav.FindLeaf(t.At + new Vec3(0, 0, 8));
            Assert.True(leaf >= 0, $"{t.Class} at {t.At} has no leaf");
            Assert.True(nav.StandableAtCentre(leaf, 32f, 72f, Nav3dClipClass.Player), $"{t.Class} at {t.At} has no floor");
        });
    }
}

/// <summary>
/// <see cref="NavPlayableSpace"/> and the grid <see cref="LevelNavFromBsp"/>
/// lays over it: the skybox and the summary stage are left out, so a map
/// whose world is too tall for a column still builds at 16-unit voxels.
/// </summary>
public sealed class NavPlayableSpaceTests(TallSkyboxMap map) : IClassFixture<TallSkyboxMap>
{
    [Fact]
    public void TheMapHasVisAndAWorldTallerThanAColumn()
    {
        Assert.NotNull(VisibilityLump.Read(map.Bsp[BspLump.Visibility]));
        DModel world = BspStructView.As<DModel>(map.Bsp[BspLump.Models])[0];
        Assert.True((world.Maxs.Z - world.Mins.Z) / 16 > Nav3dFormat.MaxColumnVoxels, $"world {world.Mins.Z}..{world.Maxs.Z}");
    }

    [Fact]
    public void ThePlaySpaceIsThePlayRoomAlone()
    {
        Box play = Assert.IsType<Box>(NavPlayableSpace.Of(map.Bsp));

        // Leaf bounds are whole units and may stand a unit proud of the room.
        Assert.InRange(play.Mins.Z, TallSkyboxMap.Play.Mins.Z - 1, TallSkyboxMap.Play.Mins.Z);
        Assert.InRange(play.Maxs.Z, TallSkyboxMap.Play.Maxs.Z, TallSkyboxMap.Play.Maxs.Z + 1);
        Assert.InRange(play.Mins.X, TallSkyboxMap.Play.Mins.X - 1, TallSkyboxMap.Play.Mins.X);
        Assert.InRange(play.Maxs.Y, TallSkyboxMap.Play.Maxs.Y, TallSkyboxMap.Play.Maxs.Y + 1);
    }

    [Fact]
    public void TheNavigationBuildsOverThePlaySpaceAtSixteenUnitVoxels()
    {
        BspNavResult result = LevelNavFromBsp.Build(map.Bsp, Nav3dBspBinding.Of(map.Bytes), null);
        Nav3dLevel level = result.Level;

        // Two voxels of margin under the floor: the grid starts at -32 and
        // runs to the ceiling's top margin, 20 voxels in all.
        Assert.Equal(-32f, level.Origin.Z);
        Assert.Equal(20, level.CellHeights is null ? level.CellVoxels : level.CellHeights.Max());
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(level));
        Assert.True(nav.FindLeaf(new Vec3(128, 128, 8)) >= 0);
        Assert.Equal(-1, nav.FindLeaf(new Vec3(128, 128, 3100)));
    }

    [Fact]
    public void AMatchSummarySpawnIsNotAPlaceAPlayerStarts()
    {
        BspEntity summary = new();
        summary.Pairs.Add(new BspKeyValue("classname", "info_player_teamspawn"));
        summary.Pairs.Add(new BspKeyValue("MatchSummary", "2"));
        BspEntity plain = new();
        plain.Pairs.Add(new BspKeyValue("classname", "info_player_teamspawn"));
        plain.Pairs.Add(new BspKeyValue("MatchSummary", "0"));

        Assert.True(NavPlayableSpace.IsMatchSummarySpawn(summary));
        Assert.False(NavPlayableSpace.IsMatchSummarySpawn(plain));
    }

    [Fact]
    public async Task AnOrdinarySpawnInTheLowRoomKeepsItInThePlaySpace()
    {
        (_, BspData bsp) = await TallSkyboxMap.CompileAsync(TallSkyboxMap.Vmf(summarySpawn: false));

        Box play = Assert.IsType<Box>(NavPlayableSpace.Of(bsp));
        Assert.True(play.Mins.Z <= -2600, $"play {play}");
        Assert.True(play.Maxs.Z < 3000, $"play {play}");
    }

    [Fact]
    public async Task AMapWithoutVisKeepsTheWorldsBounds()
    {
        VbspResult result = await RoomHarness.CompileAsync(TallSkyboxMap.Vmf(), await RoomHarness.ContextAsync());

        Assert.Null(NavPlayableSpace.Of(result.Bsp!));
    }

    [Fact]
    public async Task TheCommandLineTurnsThePlaySpaceOff()
    {
        InMemoryFileSystem fs = new();
        const string bsp = "/tall/maps/tall.bsp";
        fs.AddFile(VPath.Create(Path.GetFullPath(bsp)).Value, map.Bytes);
        using StringWriter on = new(), off = new();

        int exit = await Nav3dCommand.RunAsync(fs, [], [bsp], on);
        Assert.True(exit == Program.ExitSuccess, on.ToString());
        Assert.Equal(RoomCommands.ExitFailed, await Nav3dCommand.RunAsync(fs, [], [bsp, "-playspace", "off", "-out", bsp + ".off"], off));
        Assert.Contains("a .nav3d column holds at most 255", off.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("info_player_teamspawn", true)]
    [InlineData("info_player_deathmatch", true)]
    [InlineData("INFO_PLAYER_START", true)]
    [InlineData("info_teleport_destination", true)]
    [InlineData("sky_camera", false)]
    [InlineData("prop_static", false)]
    public void PlayerSeedsAreTheSpawnsAndTeleportDestinations(string className, bool seed) =>
        Assert.Equal(seed, NavPlayableSpace.IsPlayerSeed(className));
}
