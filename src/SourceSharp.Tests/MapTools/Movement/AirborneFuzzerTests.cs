//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapTools.Movement;
using Xunit;

namespace SourceSharp.Tests.MapTools.Movement;

/// <summary>
/// Facts for <see cref="AirborneFuzzer"/>: each event kind from a flight
/// built to meet it, the explosive-only floor, and the fuzz's determinism.
/// </summary>
public class AirborneFuzzerTests
{
    private const int Solid = 1;
    private static readonly MovementProfile Soldier = MovementProfile.Tf2("soldier");

    private static NavBrush Box(float x0, float y0, float z0, float x1, float y1, float z1) =>
        NavBrush.Box(new Vec3(x0, y0, z0), new Vec3(x1, y1, z1), Solid);

    private static NavBrush Floor => Box(-2048, -2048, -64, 2048, 2048, 0);

    private static (List<AirborneEvent> Events, Vec3? Landed) Fly(LaunchKind launch, float speed, float elevation, params NavBrush[] solids) =>
        AirborneFuzzer.Fly(new PlayerMoveSim(new HullTraceWorld([Floor, .. solids]), Soldier), Vec3.Zero, 0, launch, speed, elevation, 0, 330);

    private static readonly LaunchKind Push = new("push", 0, 0, 0, 0, 0, false);

    [Fact]
    public void AFlightIntoAPostIsAnEdgeCatch()
    {
        (List<AirborneEvent> events, _) = Fly(Push, 700, 12, Box(250, -8, 0, 266, 8, 200));

        AirborneEvent e = Assert.Single(events, x => x.Kind == AirborneEventKind.EdgeCatch);
        Assert.Equal(1, e.Brush);
        Assert.Equal("push", e.Launch);
    }

    [Fact]
    public void AFlightIntoALongWallIsNotACatch()
    {
        (List<AirborneEvent> events, _) = Fly(Push, 700, 12, Box(250, -512, 0, 266, 512, 400));

        Assert.DoesNotContain(events, x => x.Kind is AirborneEventKind.EdgeCatch or AirborneEventKind.LipCatch);
    }

    [Fact]
    public void ClippingALedgesTopIsALipCatch()
    {
        // A flight that clears the run-up's ground but meets a ledge whose
        // top is just above its feet.
        (List<AirborneEvent> events, _) = Fly(Push, 700, 18, Box(300, -512, 0, 1000, 512, 60));

        Assert.Contains(events, x => x.Kind == AirborneEventKind.LipCatch);
    }

    [Fact]
    public void FlyingUpIntoALowCeilingIsABonk()
    {
        (List<AirborneEvent> events, _) = Fly(LaunchKind.RocketJump, 900, 85, Box(-1024, -1024, 200, 1024, 1024, 260));

        Assert.Contains(events, x => x.Kind == AirborneEventKind.CeilingBonk);
        Assert.False(AirborneFuzzer.IsProblem(AirborneEventKind.CeilingBonk));
    }

    [Fact]
    public void AVTroughWedgesAPlayer()
    {
        // Two 60-degree slopes meeting in a crease along Y at x = 400: too
        // steep to stand on, so a player who lands in it slides to the crease
        // and stays there in the air. The near side is low enough to fly
        // over; the far side rises out of reach so the flight comes down in
        // the trough.
        float s = MathF.Sin(60f * MathF.PI / 180f), c = MathF.Cos(60f * MathF.PI / 180f);
        NavBrush near = NavBrush.FromPlanes(
            [(new Vec3(s, 0, c), 400 * s), (new Vec3(0, 0, -1), 0f), (new Vec3(-1, 0, 0), -300f), (new Vec3(1, 0, 0), 400f), (new Vec3(0, 1, 0), 512f), (new Vec3(0, -1, 0), 512f)],
            Solid)!;
        NavBrush far = NavBrush.FromPlanes(
            [(new Vec3(-s, 0, c), -400 * s), (new Vec3(0, 0, -1), 0f), (new Vec3(1, 0, 0), 1000f), (new Vec3(-1, 0, 0), -400f), (new Vec3(0, 1, 0), 512f), (new Vec3(0, -1, 0), 512f)],
            Solid)!;

        (List<AirborneEvent> events, Vec3? landed) = Fly(Push, 700, 70, near, far);

        Assert.Contains(events, x => x.Kind == AirborneEventKind.Wedged);
        Assert.Null(landed);
    }

    [Fact]
    public void AFlightOverOpenGroundLandsWithNoEvents()
    {
        (List<AirborneEvent> events, Vec3? landed) = Fly(LaunchKind.RocketJump, 800, 60);

        Assert.Empty(events);
        Assert.NotNull(landed);
        Assert.True(landed!.Value.X > 300f);
    }

    [Fact]
    public void AFlightFromNoFloorDoesNothing()
    {
        (List<AirborneEvent> events, Vec3? landed) = AirborneFuzzer.Fly(
            new PlayerMoveSim(new HullTraceWorld([]), Soldier), new Vec3(0, 0, 500), 0, Push, 500, 10, 0, 330);

        Assert.Empty(events);
        Assert.Null(landed);
    }

    [Fact]
    public void FloorOnlyBlastJumpsReachIsReported()
    {
        // A tall tower in an open floor: walks and jumps cannot get on top,
        // rocket jumps can.
        HullTraceWorld world = new([Floor, Box(100, -128, 0, 356, 128, 300)]);
        ReachabilityResult reach = Reachability.Analyze(world, Soldier, [new ReachSpawn(2, new Vec3(-300, 0, 0))],
            new Vec3(-600, -400, -10), new Vec3(600, 400, 500), ReachabilityOptions.Default with { Spacing = 64 });

        AirborneResult air = AirborneFuzzer.Analyze(world, Soldier, reach, AirborneOptions.Default with
        {
            Trajectories = 3000,
            Launches = [LaunchKind.RocketJump, LaunchKind.StickyJump],
        });

        Assert.NotEmpty(air.ExplosiveOnly);
        Assert.All(air.ExplosiveOnly, n => Assert.True(reach.Nodes[n].Z > 290));

        // The tower's whole top is one area, though flights landed on only
        // parts of it: from where they landed a player walks the rest.
        NodeGroup top = Assert.Single(air.BlastOnlyAreas);
        Assert.Equal(air.ExplosiveOnly, top.Nodes);
        Assert.Equal(reach.Nodes.Count(p => p.Z > 290), top.Nodes.Count);
    }

    [Fact]
    public void TheSameSeedGivesTheSameResultAtAnyThreadCount()
    {
        HullTraceWorld world = new([Floor, Box(200, -8, 0, 216, 8, 200), Box(-300, -300, 150, 300, 300, 180)]);
        ReachabilityResult reach = Reachability.Analyze(world, Soldier, [new ReachSpawn(2, Vec3.Zero)],
            new Vec3(-500, -500, -10), new Vec3(500, 500, 300), ReachabilityOptions.Default with { Spacing = 64 });

        AirborneResult one = AirborneFuzzer.Analyze(world, Soldier, reach, AirborneOptions.Default with { Trajectories = 400, MaxDegreeOfParallelism = 1 });
        AirborneResult many = AirborneFuzzer.Analyze(world, Soldier, reach, AirborneOptions.Default with { Trajectories = 400, MaxDegreeOfParallelism = 8 });
        AirborneResult other = AirborneFuzzer.Analyze(world, Soldier, reach, AirborneOptions.Default with { Trajectories = 400, Seed = 99 });

        Assert.Equal(Describe(one), Describe(many));
        Assert.NotEqual(Describe(one), Describe(other));
        Assert.NotEmpty(one.Clusters);
        Assert.Equal(one.Clusters.OrderByDescending(c => c.Count).Select(c => c.Count), one.Clusters.Select(c => c.Count));
    }

    [Fact]
    public void NoStartsOrNoFlightsGiveAnEmptyResult()
    {
        HullTraceWorld world = new([Floor]);
        ReachabilityResult nobody = Reachability.Analyze(world, Soldier, [], new Vec3(-100, -100, -10), new Vec3(100, 100, 100));

        Assert.Equal(0, AirborneFuzzer.Analyze(world, Soldier, nobody).Trajectories);
    }

    [Theory]
    [InlineData(16, 16, true)]
    [InlineData(16, 48, true)]
    [InlineData(16, 512, false)]
    [InlineData(64, 64, false)]
    public void OnlyPostsAndShortPiecesAreSmall(float a, float b, bool small) =>
        Assert.Equal(small, AirborneFuzzer.IsSmall(Box(0, 0, 0, a, b, 100)));

    [Fact]
    public void Tf2LaunchesCarryTheirRanges()
    {
        Assert.Equal(5, LaunchKind.Tf2.Count);
        Assert.Equal(650f, LaunchKind.RocketJump.MaxSpeed); // the calibration measured pushes of about 430 and 615
        Assert.Equal(289f - (1.5f * 800f / 66f), AirborneFuzzer.JumpSpeedAtPush(Soldier), 3);
        Assert.Equal(2000f, LaunchKind.StickyJump.MaxSpeed);
        Assert.False(LaunchKind.Airblast.RunningJump);
        Assert.All(LaunchKind.Tf2, l => Assert.True(l.MinSpeed <= l.MaxSpeed && l.MinElevation <= l.MaxElevation));
    }

    [Fact]
    public void BlastOnlyFloorSpreadsAlongWalksFromWhereFlightsLand()
    {
        // 0 and 1 are reached on foot. A flight lands on 2; from 2 a player
        // walks to 3 and on to 1, back into the play area; 4 is reached by
        // nothing. Landings on reached floor or on no floor add nothing.
        Vec3[] nodes = [new(0, 0, 0), new(64, 0, 0), new(0, 64, 100), new(64, 64, 100), new(500, 500, 0)];
        int[][] edges = [[1], [0], [3], [1], []];
        ReachabilityResult reach = new(nodes, edges, new Dictionary<int, bool[]> { [2] = [true, true, false, false, false] }, [0], [], []);

        bool[] blastOnly = AirborneFuzzer.BlastOnly(reach, [true, true, false, false, false], [2, -1, 0, 2]);

        Assert.Equal([false, false, true, true, false], blastOnly);
    }

    [Fact]
    public void ABlastJumpGetsOutOfAShallowPitButNotADeepOne()
    {
        // Two pits beside a floor a spawn stands on, each walked into and not
        // out of: one 160 deep, which rocket and sticky jumps clear, and one
        // 1400 deep, which nothing clears.
        NavBrush[] solids =
        [
            Box(-1024, -512, -64, 0, 512, 0),
            Box(0, -512, -224, 256, 0, -160), Box(0, 0, -1464, 256, 512, -1400),
            Box(0, -16, -1464, 256, 0, 0), Box(256, -512, -1464, 272, 512, 0),
        ];
        HullTraceWorld world = new(solids);
        ReachabilityResult reach = Reachability.Analyze(world, Soldier, [new ReachSpawn(2, new Vec3(-300, 0, 0))],
            new Vec3(-600, -480, -1450), new Vec3(240, 480, 200), ReachabilityOptions.Default with { Spacing = 64 });
        Assert.Equal(2, reach.Traps.Count);

        AirborneResult air = AirborneFuzzer.Analyze(world, Soldier, reach, AirborneOptions.Default with { Trajectories = 50, TrapFlights = 60 });

        int shallow = reach.Traps[0].Sample.Z > -500 ? 0 : 1;
        Assert.Contains("rocket_jump", air.TrapEscapes[shallow]);
        Assert.Empty(air.TrapEscapes[1 - shallow]);
    }

    [Fact]
    public void TrapEscapesNeedFlightsAndALimitAboveZero()
    {
        HullTraceWorld world = new([Floor, Box(100, -100, -300, 300, 100, -200)]);
        ReachabilityResult reach = new(
            [new(0, 0, 0), new(200, 0, -200)], [[1], []], new Dictionary<int, bool[]> { [2] = [true, true] }, [0], [], [new NodeGroup([1], new(200, 0, -200), new(200, 0, -200), new(200, 0, -200))]);

        Assert.Empty(AirborneFuzzer.Analyze(world, Soldier, reach, AirborneOptions.Default with { Trajectories = 10, TrapFlights = 0 }).TrapEscapes[0]);
        Assert.Empty(AirborneFuzzer.Analyze(world, Soldier, reach, AirborneOptions.Default with { Trajectories = 10, TrapLimit = 0 }).TrapEscapes[0]);
        Assert.Single(AirborneFuzzer.Analyze(world, Soldier, reach, AirborneOptions.Default with { Trajectories = 0 }).TrapEscapes);
    }

    private static string Describe(AirborneResult r) =>
        string.Join(";", r.Clusters.Select(c => $"{c.Kind}:{c.Count}:{c.Centre}")) + "|" + string.Join(",", r.ExplosiveOnly);
}
