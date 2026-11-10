//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Text;

using SourceSharp.MapTools.Rooms;

using Xunit;
using Xunit.Abstractions;

using static SourceSharp.Tests.MapTools.Rooms.MultiCellHarness;

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Lighting of a room larger than one cell (the rooms design, 17.7 option
/// A's lighting line and section 9): one base bake over the whole room,
/// exact inside it at every turn, and door light to a neighbour of one cell
/// meeting PR 10's tolerances. The 2 x 2 hall has a lamp in one cell, the
/// hub a lamp of its own.
/// </summary>
public sealed class MultiCellLightTests(MultiCellLightTests.Fixture fixture, ITestOutputHelper output)
    : IClassFixture<MultiCellLightTests.Fixture>
{
    /// <summary>The four quarter turns, in degrees.</summary>
    public static TheoryData<int> Rotations => new() { 0, 90, 180, 270 };

    /// <summary>
    /// The hall alone, every socket capped, links to vrad of its own linked
    /// map at every turn: the same styles and luxels on every face, the lamp
    /// lighting the cells it does not stand in through the open room, and
    /// the same world lights, leaves and map flags.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rotations))]
    public async Task ACappedHallLinksToVradOfItsOwnLinkAtEveryTurn(int rotation)
    {
        LevelGrid level = RoomHeightHarness.Level("+, +", $"hall@{rotation}, +");
        BspData linked = (await RoomLightHarness.LinkAsync(fixture.Lit, level)).Bsp;
        BspData relit = await RoomLightHarness.RelightAsync((await RoomLightHarness.LinkAsync(fixture.Unlit, level)).Bsp);

        (bool styles, int luxels, int differ, int thin) = LitCompare.SameFaces(linked, relit);
        output.WriteLine($"hall@{rotation}: {luxels} luxels, {differ} differ, {thin} on thin faces differ");
        Assert.True(styles);
        Assert.True(luxels > 0);
        Assert.Equal(0, differ);
        foreach (BspLump lump in (ReadOnlySpan<BspLump>)[BspLump.WorldLights, BspLump.Leafs, BspLump.MapFlags])
        {
            Assert.True(linked[lump].Data.Span.SequenceEqual(relit[lump].Data.Span), $"hall@{rotation}: {lump}");
        }
    }

    /// <summary>
    /// The hall jointed to the hub at its east0 door, against vrad of the
    /// linked level: PR 10's tolerances hold, and the base alone is further
    /// off near the joint: each room's lamp reaches the other through the
    /// door.
    /// </summary>
    [Fact]
    public async Task AHallsDoorLightMeetsTheTolerances()
    {
        LevelGrid level = RoomHeightHarness.Level("+, +, ~", "hall, +, hub");
        LinkedLevel door = await RoomLightHarness.LinkAsync(fixture.DoorLit, level);
        List<DoorLightCompare.Joint> joints = DoorLightCompare.Joints(door);
        Assert.Equal(2, joints.Count);
        BspData relit = await RoomLightHarness.RelightAsync((await RoomLightHarness.LinkAsync(fixture.Unlit, level)).Bsp);
        BspData base0 = (await RoomLightHarness.LinkAsync(fixture.Lit, level)).Bsp;

        DoorLightCompare.Metric withDoor = DoorLightCompare.Measure(door.Bsp, relit, joints, allStyles: true);
        DoorLightCompare.Metric alone = DoorLightCompare.Measure(base0, relit, joints, allStyles: true);
        output.WriteLine($"door light: {withDoor}");
        output.WriteLine($"base only:  {alone}");
        Assert.True(withDoor.NearP95 <= 0.08, $"near p95 {withDoor.NearP95}");
        Assert.True(withDoor.FarP95 <= 0.08, $"far p95 {withDoor.FarP95}");
        Assert.InRange(withDoor.Energy, 0.98, 1.02);
        Assert.True(withDoor.Brighter <= withDoor.Count * 3 / 100, $"{withDoor.Brighter} of {withDoor.Count} brighter");
        Assert.True(alone.NearP95 > withDoor.NearP95, $"base near p95 {alone.NearP95}");
    }

    /// <summary>The library, compiled lit, lit with its door light, and unlit, once for the class.</summary>
    public sealed class Fixture : IAsyncLifetime
    {
        /// <summary>The library: the hub with a lamp, then the hall with a lamp in its south-east cell; the sun in the gap.</summary>
        public VmfDocument Library { get; } = MakeLibrary();

        /// <summary>The rooms, lit.</summary>
        public RoomLibrary Lit { get; private set; } = null!;

        /// <summary>The rooms, lit with their door light.</summary>
        public RoomLibrary DoorLit { get; private set; } = null!;

        /// <summary>The rooms, unlit.</summary>
        public RoomLibrary Unlit { get; private set; } = null!;

        /// <inheritdoc/>
        public async Task InitializeAsync()
        {
            Lit = await RoomLightHarness.CompileAsync(Library);
            DoorLit = await RoomLightHarness.CompileAsync(Library, doorLight: true);
            Unlit = await RoomLightHarness.CompileAsync(Library, light: false);
        }

        /// <inheritdoc/>
        public Task DisposeAsync() => Task.CompletedTask;

        private static VmfDocument MakeLibrary()
        {
            VmfDocument library = RoomHarness.LibraryVmf(Hub, Hall);
            float hall = RoomHarness.Cell + RoomHarness.LibraryGap;
            library.Chunks.Add(RoomLightHarness.Light(800, new Vec3(128, 128, 150)));
            library.Chunks.Add(RoomLightHarness.Light(810, new Vec3(hall + 380, 120, 150)));
            library.Chunks.Add(RoomLightHarness.Sun());
            RoomLightHarness.WorldAlign(library);
            return library;
        }
    }
}
