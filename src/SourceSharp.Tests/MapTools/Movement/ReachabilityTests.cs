//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapTools.Movement;
using SourceSharp.Tests.MapPreview;
using Xunit;

namespace SourceSharp.Tests.MapTools.Movement;

/// <summary>
/// Facts for <see cref="Reachability"/> over small worlds whose answers are
/// known: open floor, a one-way drop, a pit with no way out, a ledge too
/// high to climb, one that needs a jump, and a stuck spawn.
/// </summary>
public class ReachabilityTests
{
    private const int Solid = 1;
    private static readonly MovementProfile Soldier = MovementProfile.Tf2("soldier");
    private static readonly ReachabilityOptions Coarse = ReachabilityOptions.Default with { Spacing = 64 };

    private static NavBrush Box(float x0, float y0, float z0, float x1, float y1, float z1) =>
        NavBrush.Box(new Vec3(x0, y0, z0), new Vec3(x1, y1, z1), Solid);

    /// <summary>A 1024-square floor at z = 0, walled in.</summary>
    private static List<NavBrush> Arena() =>
    [
        Box(-512, -512, -64, 512, 512, 0),
        Box(-576, -576, 0, -512, 576, 400), Box(512, -576, 0, 576, 576, 400),
        Box(-576, -576, 0, 576, -512, 400), Box(-576, 512, 0, 576, 576, 400),
    ];

    private static ReachabilityResult Analyze(List<NavBrush> solids, Vec3 spawn, ReachabilityOptions? options = null) =>
        Reachability.Analyze(new HullTraceWorld(solids), Soldier, [new ReachSpawn(2, spawn)],
            new Vec3(-480, -480, -900), new Vec3(480, 480, 380), options ?? Coarse);

    private static int NodeNear(ReachabilityResult r, float x, float y, float z) =>
        Enumerable.Range(0, r.Nodes.Count).OrderBy(i => (r.Nodes[i] - new Vec3(x, y, z)).LengthSquared()).First();

    [Fact]
    public void AnOpenFloorIsOnePlayArea()
    {
        ReachabilityResult r = Analyze(Arena(), new Vec3(0, 0, 0));

        Assert.True(r.Nodes.Count > 100);
        Assert.All(r.ReachedBy[2], Assert.True);
        Assert.Empty(r.Islands);
        Assert.Empty(r.Traps);
        Assert.True(r.SpawnNodes[0] >= 0);
    }

    [Fact]
    public void ADropCanBeWalkedOffButNotClimbedBack()
    {
        // A 128-high platform on the east half; the spawn is on top of it.
        List<NavBrush> world = [.. Arena(), Box(0, -512, 0, 512, 512, 128)];

        ReachabilityResult r = Analyze(world, new Vec3(256, 0, 128));

        int top = NodeNear(r, 256, 0, 128), bottom = NodeNear(r, -256, 0, 0);
        Assert.True(r.ReachedBy[2][bottom], "the lower floor is reached by dropping off");
        Assert.True(r.ReachedBy[2][top]);

        ReachabilityResult fromBelow = Analyze(world, new Vec3(-256, 0, 0));
        Assert.False(fromBelow.ReachedBy[2][NodeNear(fromBelow, 256, 0, 128)], "128 is too high to climb back");
        Assert.Contains(fromBelow.Islands, g => g.Nodes.Contains(NodeNear(fromBelow, 256, 0, 128)));
    }

    [Fact]
    public void AStepTooHighToWalkIsReachedByJumping()
    {
        // A 50-unit ledge: above the 18 step, below the 52 jump.
        List<NavBrush> world = [.. Arena(), Box(128, -512, 0, 512, 512, 50)];

        ReachabilityResult walking = Analyze(world, new Vec3(-256, 0, 0), Coarse with { Jumps = false });
        ReachabilityResult jumping = Analyze(world, new Vec3(-256, 0, 0));

        Assert.False(walking.ReachedBy[2][NodeNear(walking, 320, 0, 50)]);
        Assert.True(jumping.ReachedBy[2][NodeNear(jumping, 320, 0, 50)]);
    }

    [Fact]
    public void APitWithNoWayOutIsATrap()
    {
        // A 128-square pit, 400 deep, in the middle of the arena.
        List<NavBrush> world =
        [
            Box(-512, -512, -64, 512, -64, 0), Box(-512, 64, -64, 512, 512, 0),
            Box(-512, -64, -64, -64, 64, 0), Box(64, -64, -64, 512, 64, 0),
            Box(-64, -64, -464, 64, 64, -400),
            Box(-128, -128, -400, -64, 128, 0), Box(64, -128, -400, 128, 128, 0),
            Box(-64, -128, -400, 64, -64, 0), Box(-64, 64, -400, 64, 128, 0),
            Box(-576, -576, 0, -512, 576, 400), Box(512, -576, 0, 576, 576, 400),
            Box(-576, -576, 0, 576, -512, 400), Box(-576, 512, 0, 576, 576, 400),
        ];

        ReachabilityResult r = Analyze(world, new Vec3(-300, -300, 0), Coarse with { Spacing = 32 });

        NodeGroup trap = Assert.Single(r.Traps);
        Assert.All(trap.Nodes, n => Assert.True(r.Nodes[n].Z < -399));
        Assert.True(r.ReachedBy[2][trap.Nodes[0]]);
    }

    [Fact]
    public void ASpawnInsideASolidStandsOnNoNode()
    {
        List<NavBrush> world = [.. Arena(), Box(-100, -100, 0, 100, 100, 200)];

        ReachabilityResult r = Analyze(world, new Vec3(0, 0, 10));

        Assert.Equal(-1, r.SpawnNodes[0]);
        Assert.All(r.ReachedBy[2], Assert.False);
    }

    [Fact]
    public void FloorsAreFoundStackedInAColumn()
    {
        HullTraceWorld world = new([Box(-100, -100, -64, 100, 100, 0), Box(-100, -100, 200, 100, 100, 232)]);

        List<Vec3> floors = Reachability.Floors(world, Soldier, (0, 0), -500, 600);

        Assert.Equal(2, floors.Count);
        Assert.Equal(232f, floors[0].Z, 0);
        Assert.Equal(0f, floors[1].Z, 0);
    }

    [Fact]
    public void SteepSurfacesAreNotFloors()
    {
        HullTraceWorld world = new([NavBrush.FromPlanes(
        [
            (new Vec3(-0.8f, 0, 0.6f), 0f), (new Vec3(0, 0, -1), 64f), (new Vec3(1, 0, 0), 300f),
            (new Vec3(-1, 0, 0), 0f), (new Vec3(0, 1, 0), 200f), (new Vec3(0, -1, 0), 200f),
        ], Solid)!]);

        Assert.Empty(Reachability.Floors(world, Soldier, (100, 0), -500, 600));
    }

    [Fact]
    public async Task SealedVoidIsNotFloor()
    {
        // The compiled room's floor slab has void under it that the
        // compiler fills solid in the tree: only the room's floor counts.
        BspData bsp = await PreviewTestMaps.RoomAsync(false, new PreviewTestMaps.TestEntity("info_player_teamspawn", [("origin", "256 256 16")]));
        (HullTraceWorld world, _) = await HullTraceWorld.FromBspAsync(bsp, null);

        List<Vec3> floors = Reachability.Floors(world, Soldier, (256, 256), -2000, 2000);

        Assert.All(floors, f => Assert.True(world.IsOpen(f + new Vec3(0, 0, 41))));
        Assert.Contains(floors, f => MathF.Abs(f.Z) < 1);
        Assert.False(world.IsOpen(new Vec3(256, 256, -1000)));
        Assert.True(new HullTraceWorld([]).IsOpen(new Vec3(0, 0, -1000)));
    }

    [Fact]
    public void StronglyConnectedComponentsFollowEdgeDirection()
    {
        // 0 <-> 1 -> 2 <-> 3, and 4 excluded.
        int[][] edges = [[1], [0, 2], [3], [2], [0]];
        bool[] include = [true, true, true, true, false];

        int[] component = Reachability.StronglyConnected(edges, include, out int count);

        Assert.Equal(2, count);
        Assert.Equal(component[0], component[1]);
        Assert.Equal(component[2], component[3]);
        Assert.NotEqual(component[0], component[2]);
        Assert.Equal(-1, component[4]);
    }

    [Fact]
    public void TheAnalysisIsTheSameAtAnyThreadCount()
    {
        List<NavBrush> world = [.. Arena(), Box(0, -512, 0, 512, 512, 40), Box(-300, -300, 0, -200, -200, 120)];

        ReachabilityResult one = Analyze(world, new Vec3(-400, 0, 0), Coarse with { MaxDegreeOfParallelism = 1 });
        ReachabilityResult many = Analyze(world, new Vec3(-400, 0, 0), Coarse with { MaxDegreeOfParallelism = 8 });

        Assert.Equal(one.Nodes, many.Nodes);
        Assert.Equal(one.Edges.Select(e => string.Join(",", e)), many.Edges.Select(e => string.Join(",", e)));
    }

    [Fact]
    public void AnalysisIsCancellable()
    {
        using CancellationTokenSource cancel = new();
        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Reachability.Analyze(
            new HullTraceWorld(Arena()), Soldier, [], new Vec3(-480, -480, -10), new Vec3(480, 480, 100), Coarse, cancel.Token));
    }
}
