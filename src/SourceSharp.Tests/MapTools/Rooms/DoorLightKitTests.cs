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

namespace SourceSharp.Tests.MapTools.Rooms;

/// <summary>
/// Door light for the kits a game uses (the rooms design, section 9; 17.8
/// stage 9): a 1024-unit cell with a 208 x 192 door standing on the floor,
/// its walls 8 or 16 deep, one door slot or the first of three. Two rooms,
/// each with a lamp, the second turned a half so their doors meet, linked
/// and held to vrad of the linked level in PR 10's terms.
/// </summary>
public sealed class DoorLightKitTests(ITestOutputHelper output)
{
    private const float Cell = 1024f;

    /// <summary>The level linked with its door light, vrad of the same level linked unlit, and the joints.</summary>
    private static async Task<(BspData Linked, BspData Relit, List<DoorLightCompare.Joint> Joints)> LinkAsync(float depth, int slots)
    {
        SocketKit kit = new(208f, 192f, depth) { Sill = DoorSill.Floor, Slots = slots };
        string mine = slots == 1 ? "east" : "east_a", theirs = slots == 1 ? "east" : "east_c";
        RoomDefinition a = new("a", Cell, kit, [new RoomSocket(RoomFacing.PositiveX, mine)]);
        RoomDefinition b = new("b", Cell, kit, [new RoomSocket(RoomFacing.PositiveX, theirs) { Slot = slots - 1 }]);
        VmfDocument library = RoomHarness.LibraryVmf(a, b);
        float second = Cell + RoomHarness.LibraryGap;
        library.Chunks.Add(RoomLightHarness.Light(800, new Vec3(640, 240, 600)));
        library.Chunks.Add(RoomLightHarness.Light(810, new Vec3(second + 360, 800, 600)));
        RoomLightHarness.WorldAlign(library);
        RoomLibrary lit = await RoomLightHarness.CompileAsync(library, doorLight: true);
        RoomLibrary unlit = await RoomLightHarness.CompileAsync(library, light: false);
        LevelGrid level = RoomHeightHarness.Level("a, b@180");
        LinkedLevel door = await RoomLightHarness.LinkAsync(lit, level);
        BspData relit = await RoomLightHarness.RelightAsync((await RoomLightHarness.LinkAsync(unlit, level)).Bsp);
        return (door.Bsp, relit, DoorLightCompare.Joints(door));
    }

    /// <summary>The worst relative error of the luxels on the joint's plane: the doorway's threshold, under and over the door.</summary>
    private static double ThresholdWorst(BspData linked, BspData relit)
    {
        var a = LitCompare.Lattice(linked);
        double worst = 0;
        foreach ((var key, (List<SourceSharp.MapFormats.Bsp.Structs.ColorRgbExp32> colours, bool thin)) in LitCompare.Lattice(relit))
        {
            if (!thin && key.Item1 == (long)(Cell * 100) && a.TryGetValue(key, out var other) && !other.Thin)
            {
                worst = Math.Max(worst, LitCompare.Relative(other.Colours[0].ToLinear(), colours[0].ToLinear()));
            }
        }

        return worst;
    }

    /// <summary>
    /// Walls 8 deep, the door on the floor: near and far p95 within 0.02
    /// (measured 0.008 and 0.000), p99 within 0.2 (0.151), energy within 1%,
    /// at most 1% of luxels brighter; and the doorway's threshold, the line
    /// of luxels on the joint's plane, within 0.35 (0.30). Red before the
    /// threshold cells were seen from inside the room: p99 0.224, the
    /// threshold 0.45.
    /// </summary>
    [Fact]
    public async Task AThinWallFloorDoorsLightMeetsItsTolerances()
    {
        (BspData linked, BspData relit, List<DoorLightCompare.Joint> joints) = await LinkAsync(8f, 1);
        Assert.Equal(2, joints.Count);
        DoorLightCompare.Metric metric = DoorLightCompare.Measure(linked, relit, joints, allStyles: true);
        double threshold = ThresholdWorst(linked, relit);
        output.WriteLine($"wall 8, floor door: {metric}; threshold worst {threshold:F3}");
        Assert.True(metric.NearP95 <= 0.02, $"near p95 {metric.NearP95}");
        Assert.True(metric.FarP95 <= 0.02, $"far p95 {metric.FarP95}");
        Assert.True(metric.NearP99 <= 0.2, $"near p99 {metric.NearP99}");
        Assert.InRange(metric.Energy, 0.99, 1.01);
        Assert.True(metric.Brighter <= metric.Count / 100, $"{metric.Brighter} of {metric.Count} brighter");
        Assert.True(threshold <= 0.35, $"threshold {threshold}");
    }

    /// <summary>
    /// Walls 16 deep and the first of three door slots (its door 51 units of
    /// wall from the side wall inside the room), meeting the third slot of
    /// the turned neighbour: near p95 within 0.02 (measured 0.007), p99
    /// within 0.05 (0.032), far p95 within 0.02, energy within 1%.
    /// </summary>
    [Fact]
    public async Task AFloorDoorAtTheFirstOfThreeSlotsMeetsItsTolerances()
    {
        (BspData linked, BspData relit, List<DoorLightCompare.Joint> joints) = await LinkAsync(16f, 3);
        Assert.Equal(2, joints.Count);
        DoorLightCompare.Metric metric = DoorLightCompare.Measure(linked, relit, joints, allStyles: true);
        output.WriteLine($"wall 16, slot a: {metric}");
        Assert.True(metric.NearP95 <= 0.02, $"near p95 {metric.NearP95}");
        Assert.True(metric.NearP99 <= 0.05, $"near p99 {metric.NearP99}");
        Assert.True(metric.FarP95 <= 0.02, $"far p95 {metric.FarP95}");
        Assert.InRange(metric.Energy, 0.99, 1.01);
    }

    /// <summary>The pull back is keyed on the kit: a wall a whole number of 16-unit luxels deep keeps every bit; 8 and 24 do not.</summary>
    [Fact]
    public void OnlyAWallOfPartLuxelsPullsBack()
    {
        RoomDefinition Room(float depth) => new("r", Cell, new SocketKit(208f, 192f, depth) { Sill = DoorSill.Floor }, [new RoomSocket(RoomFacing.PositiveX, "east")]);
        Assert.False(RoomDoorLight.PullsBackFromThePlane(DoorFrame.Of(Room(16), Room(16).Sockets[0])));
        Assert.False(RoomDoorLight.PullsBackFromThePlane(DoorFrame.Of(Room(32), Room(32).Sockets[0])));
        Assert.True(RoomDoorLight.PullsBackFromThePlane(DoorFrame.Of(Room(8), Room(8).Sockets[0])));
        Assert.True(RoomDoorLight.PullsBackFromThePlane(DoorFrame.Of(Room(24), Room(24).Sockets[0])));
    }
}
