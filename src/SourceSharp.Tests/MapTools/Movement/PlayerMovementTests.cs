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
/// Facts for <see cref="HullTraceWorld"/>, <see cref="PlayerMoveSim"/> and
/// <see cref="MovementProfile"/> over hand-built worlds, with the expected
/// numbers worked from the movement rules: a TF2 jump's 52-unit peak, the
/// 18-unit step, the 0.7 floor slope.
/// </summary>
public class PlayerMovementTests
{
    private const int Solid = 1;
    private static readonly MovementProfile Soldier = MovementProfile.Tf2("soldier");
    private static readonly Vec3 Mins = Soldier.Mins(false);
    private static readonly Vec3 Maxs = Soldier.Maxs(false);

    /// <summary>A floor slab whose top is z = 0, 4096 across.</summary>
    private static NavBrush Floor(float top = 0) => NavBrush.Box(new Vec3(-2048, -2048, top - 64), new Vec3(2048, 2048, top), Solid);

    private static NavBrush Box(float x0, float y0, float z0, float x1, float y1, float z1) =>
        NavBrush.Box(new Vec3(x0, y0, z0), new Vec3(x1, y1, z1), Solid);

    /// <summary>A ramp rising along +X from z 0 at x 0 at the given angle, 512 long.</summary>
    private static NavBrush Ramp(float degrees)
    {
        float rad = degrees * MathF.PI / 180f;
        Vec3 n = new(-MathF.Sin(rad), 0, MathF.Cos(rad));
        return NavBrush.FromPlanes(
            [
                (n, 0f),
                (new Vec3(0, 0, -1), 64f),
                (new Vec3(1, 0, 0), 512f),
                (new Vec3(-1, 0, 0), 0f),
                (new Vec3(0, 1, 0), 256f),
                (new Vec3(0, -1, 0), 256f),
            ],
            Solid)!;
    }

    private static PlayerMoveSim Sim(MovementProfile profile, params NavBrush[] solids) => new(new HullTraceWorld(solids), profile);

    private static PlayerState Run(PlayerMoveSim sim, PlayerState state, MoveInput input, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            state = sim.Tick(state, input);
        }

        return state;
    }

    [Fact]
    public void ATraceThroughNothingGoesAllTheWay()
    {
        HullTrace t = new HullTraceWorld([]).Trace(Vec3.Zero, new Vec3(100, 0, 0), Mins, Maxs);
        Assert.Equal(1f, t.Fraction);
        Assert.False(t.Hit);
        Assert.Equal(-1, t.Brush);
    }

    [Fact]
    public void AFallingBoxStopsJustAboveTheFloor()
    {
        HullTrace t = new HullTraceWorld([Floor()]).Trace(new Vec3(0, 0, 100), new Vec3(0, 0, -100), Mins, Maxs);

        Assert.True(t.Hit);
        Assert.Equal(new Vec3(0, 0, 1), t.Normal);
        Assert.InRange(t.End.Z, 0f, HullTraceWorld.DistEpsilon * 1.01f);
        Assert.Equal(0, t.Brush);
    }

    [Fact]
    public void ABoxRestingOnTheFloorSlidesOverItFreely()
    {
        HullTrace t = new HullTraceWorld([Floor()]).Trace(new Vec3(0, 0, 0.03125f), new Vec3(500, 0, 0.03125f), Mins, Maxs);
        Assert.False(t.Hit);
    }

    [Fact]
    public void ABoxInsideASolidIsStuck()
    {
        HullTraceWorld world = new([Box(-100, -100, -100, 100, 100, 100)]);

        HullTrace still = world.Trace(Vec3.Zero, Vec3.Zero, Mins, Maxs);
        HullTrace leaving = world.Trace(Vec3.Zero, new Vec3(500, 0, 0), Mins, Maxs);

        Assert.True(still.AllSolid);
        Assert.True(world.IsStuck(Vec3.Zero, Mins, Maxs));
        Assert.True(leaving.StartSolid);
        Assert.False(leaving.AllSolid);
    }

    [Fact]
    public void TheBoxsExtentIsWhatTouches()
    {
        // A wall at x = 100: the box's +X face (24 out) meets it at x = 76.
        HullTrace t = new HullTraceWorld([Box(100, -200, 0, 200, 200, 200)]).Trace(new Vec3(0, 0, 10), new Vec3(300, 0, 10), Mins, Maxs);

        Assert.Equal(new Vec3(-1, 0, 0), t.Normal);
        Assert.InRange(t.End.X, 76f - 0.05f, 76f);
    }

    [Fact]
    public void BoundingBevelsStopPhantomHitsBeyondASlantedSolid()
    {
        // Past the high end of a 30-degree ramp (which ends at x = 512) the
        // ramp's slope plane, pushed out by the box, would extend upward
        // forever; the ramp's bounding planes cut it off.
        HullTraceWorld world = new([Ramp(30)]);
        float top = 512f * MathF.Tan(30f * MathF.PI / 180f);

        HullTrace above = world.Trace(new Vec3(600, 0, top - 10), new Vec3(600, 0, top - 80), Mins, Maxs);

        Assert.False(above.Hit);
    }

    [Fact]
    public void AFarSolidIsFoundAcrossIndexCells()
    {
        HullTrace t = new HullTraceWorld([Box(1800, -50, 0, 1900, 50, 100)]).Trace(new Vec3(0, 0, 10), new Vec3(2000, 0, 10), Mins, Maxs);
        Assert.True(t.Hit);
        Assert.InRange(t.End.X, 1775f, 1776f);
    }

    [Fact]
    public void ASpawnedPlayerStandsAndStaysPut()
    {
        PlayerMoveSim sim = Sim(Soldier, Floor());

        PlayerState state = Run(sim, sim.Spawn(new Vec3(0, 0, 1)), default, 66);

        Assert.True(state.OnGround);
        Assert.Equal(Vec3.Zero, state.Velocity);
        Assert.InRange(state.Origin.Z, 0f, 0.05f);
    }

    [Fact]
    public void WalkingReachesTheClassSpeedAndFrictionStopsIt()
    {
        PlayerMoveSim sim = Sim(Soldier, Floor());
        PlayerState state = Run(sim, sim.Spawn(Vec3.Zero), new MoveInput(0, 450, 0, false, false), 66);

        Assert.Equal(240f, state.Velocity.X, 1);
        Assert.Equal(0f, state.Velocity.Y, 3);

        state = Run(sim, state, default, 33);
        Assert.Equal(Vec3.Zero, state.Velocity);
    }

    [Fact]
    public void ViewYawTurnsTheWalk()
    {
        PlayerMoveSim sim = Sim(Soldier, Floor());
        PlayerState state = Run(sim, sim.Spawn(Vec3.Zero), new MoveInput(90, 450, 0, false, false), 66);

        Assert.Equal(0f, state.Velocity.X, 2);
        Assert.Equal(240f, state.Velocity.Y, 1);
    }

    [Fact]
    public void ATf2JumpPeaksAtAboutFiftyTwoUnits()
    {
        PlayerMoveSim sim = Sim(Soldier, Floor());
        PlayerState state = sim.Spawn(Vec3.Zero);
        float peak = 0;
        state = sim.Tick(state, new MoveInput(0, 0, 0, true, false));
        for (int i = 0; i < 80; i++)
        {
            state = sim.Tick(state, new MoveInput(0, 0, 0, true, false));
            peak = Math.Max(peak, state.Origin.Z);
        }

        Assert.InRange(peak, 50f, 53f); // 289² / (2 · 800) = 52.2
        Assert.True(state.OnGround);
    }

    [Fact]
    public void AJumpNeedsAFreshPress()
    {
        PlayerMoveSim sim = Sim(Soldier, Floor());
        PlayerState state = sim.Spawn(Vec3.Zero) with { JumpHeld = true };

        state = sim.Tick(state, new MoveInput(0, 0, 0, true, false));

        Assert.True(state.OnGround);
    }

    [Fact]
    public void ACrouchJumpRaisesTheFeetByTheHullDifference()
    {
        PlayerMoveSim sim = Sim(Soldier, Floor());
        PlayerState state = sim.Spawn(Vec3.Zero);
        float peak = 0;
        state = sim.Tick(state, new MoveInput(0, 0, 0, true, false));
        for (int i = 0; i < 40; i++)
        {
            state = sim.Tick(state, new MoveInput(0, 0, 0, true, true));
            peak = Math.Max(peak, state.Origin.Z);
        }

        Assert.True(state.Ducked);
        Assert.InRange(peak, 70f, 73f); // 52 plus the 20 the feet come up
    }

    [Fact]
    public void TheAirCrouchLimitHolds()
    {
        PlayerMoveSim sim = Sim(Soldier, Floor());
        PlayerState state = new(new Vec3(0, 0, 500), Vec3.Zero, false);
        for (int i = 0; i < 6; i++)
        {
            state = sim.Tick(state, new MoveInput(0, 0, 0, false, i % 2 == 0));
        }

        Assert.Equal(2, state.AirDucks);
    }

    [Fact]
    public void ATf2PlayerCrouchedOnTheGroundCannotJumpButAnHl2OneCan()
    {
        PlayerMoveSim tf = Sim(Soldier, Floor());
        PlayerMoveSim hl = Sim(MovementProfile.Hl2mp, Floor());

        PlayerState tfState = tf.Tick(tf.Tick(tf.Spawn(Vec3.Zero), new MoveInput(0, 0, 0, false, true)), new MoveInput(0, 0, 0, true, true));
        PlayerState hlState = hl.Tick(hl.Tick(hl.Spawn(Vec3.Zero), new MoveInput(0, 0, 0, false, true)), new MoveInput(0, 0, 0, true, true));

        Assert.True(tfState.OnGround);
        Assert.False(hlState.OnGround);
    }

    [Fact]
    public void CrouchingOnTheGroundSlowsTheWalkAndStandingUpNeedsRoom()
    {
        // A crawlspace from x = 100 on, 70 high: under the standing hull, over the crouched one.
        PlayerMoveSim sim = Sim(Soldier, Floor(), Box(100, -500, 70, 1000, 500, 100));
        PlayerState state = Run(sim, sim.Spawn(Vec3.Zero), new MoveInput(0, 450, 0, false, true), 132);

        Assert.Equal(80f, state.Velocity.X, 1);
        Assert.True(state.Origin.X > 150f);
        state = sim.Tick(state, new MoveInput(0, 0, 0, false, false));
        Assert.True(state.Ducked); // the ceiling at 70 is lower than the 82 standing hull
    }

    [Fact]
    public void AScoutJumpsOnceMoreInTheAir()
    {
        PlayerMoveSim scout = Sim(MovementProfile.Tf2("scout"), Floor());
        PlayerState state = scout.Tick(scout.Spawn(Vec3.Zero), new MoveInput(0, 0, 0, true, false));
        state = Run(scout, state, new MoveInput(0, 0, 0, false, false), 10);

        PlayerState second = scout.Tick(state, new MoveInput(90, 450, 0, true, false));
        PlayerState third = scout.Tick(second with { JumpHeld = false }, new MoveInput(90, 450, 0, true, false));

        Assert.True(second.AirJumped);
        Assert.True(second.Velocity.Z > state.Velocity.Z);
        Assert.Equal(400f, second.Velocity.Y, 0);
        Assert.True(third.Velocity.Z < second.Velocity.Z); // no third jump
    }

    [Theory]
    [InlineData(18f, true)]
    [InlineData(19f, false)]
    public void AStepUpToEighteenUnitsIsWalkedUp(float height, bool climbs)
    {
        PlayerMoveSim sim = Sim(Soldier, Floor(), Box(100, -500, 0, 1000, 500, height));

        PlayerState state = Run(sim, sim.Spawn(Vec3.Zero), new MoveInput(0, 450, 0, false, false), 66);

        Assert.Equal(climbs, state.Origin.X > 100);
        Assert.Equal(climbs, state.Origin.Z > height - 0.5f);
    }

    [Theory]
    [InlineData(44f, true)]
    [InlineData(46f, false)]
    public void SlopesUpToAboutFortyFiveDegreesAreFloor(float degrees, bool floor)
    {
        PlayerMoveSim sim = Sim(Soldier, Ramp(degrees));
        float z = (200f * MathF.Tan(degrees * MathF.PI / 180f)) + 30f;

        PlayerState state = Run(sim, new PlayerState(new Vec3(200, 0, z), Vec3.Zero, false), default, 40);

        Assert.Equal(floor, state.OnGround);
        if (!floor)
        {
            Assert.True(state.Origin.X < 200f, "a player on a slope too steep slides down it");
        }
    }

    [Fact]
    public void WalkingIntoAWallAtAnAngleSlidesAlongIt()
    {
        PlayerMoveSim sim = Sim(Soldier, Floor(), Box(100, -2000, 0, 200, 2000, 300));
        List<MoveContact> contacts = [];
        PlayerState state = sim.Spawn(Vec3.Zero);
        for (int i = 0; i < 66; i++)
        {
            state = sim.Tick(state, new MoveInput(45, 450, 0, false, false), contacts);
        }

        Assert.InRange(state.Origin.X, 75f, 76f);
        Assert.True(state.Origin.Y > 100f);
        Assert.Contains(contacts, c => c.Normal == new Vec3(-1, 0, 0) && c.Brush == 1);
    }

    [Fact]
    public void ACornerStopsTheWalk()
    {
        PlayerMoveSim sim = Sim(Soldier, Floor(), Box(100, -2000, 0, 200, 2000, 300), Box(-2000, 100, 0, 2000, 200, 300));

        PlayerState state = Run(sim, sim.Spawn(Vec3.Zero), new MoveInput(45, 450, 0, false, false), 66);

        Assert.InRange(state.Origin.X, 75f, 76f);
        Assert.InRange(state.Origin.Y, 75f, 76f);
    }

    [Fact]
    public void ALowCeilingBonksAJump()
    {
        PlayerMoveSim sim = Sim(Soldier, Floor(), Box(-500, -500, 100, 500, 500, 150));
        PlayerState state = sim.Tick(sim.Spawn(Vec3.Zero), new MoveInput(0, 0, 0, true, false));
        float peak = 0;
        for (int i = 0; i < 40; i++)
        {
            state = sim.Tick(state, default);
            peak = Math.Max(peak, state.Origin.Z);
        }

        Assert.InRange(peak, 17f, 18.1f); // the 82 hull meets the ceiling at 100
    }

    [Fact]
    public void SpeedIsClampedToTheMaximum()
    {
        PlayerMoveSim sim = Sim(Soldier);
        PlayerState state = sim.Tick(new PlayerState(new Vec3(0, 0, 1000), new Vec3(9000, -9000, 0), false), default);

        Assert.Equal(3500f, state.Velocity.X);
        Assert.Equal(-3500f, state.Velocity.Y);
    }

    [Fact]
    public void ClipVelocityRemovesTheIntoPartOnly()
    {
        Vec3 clipped = PlayerMoveSim.ClipVelocity(new Vec3(100, 50, -30), new Vec3(0, 0, 1), 1);
        Assert.Equal(new Vec3(100, 50, 0), clipped);
        Vec3 bounced = PlayerMoveSim.ClipVelocity(new Vec3(0, 0, -100), new Vec3(0, 0, 1), 1.5f);
        Assert.Equal(50f, bounced.Z, 3);
    }

    [Fact]
    public void ARunIsTheSameEveryTime()
    {
        PlayerMoveSim sim = Sim(Soldier, Floor(), Box(300, -40, 0, 340, 40, 16), Ramp(30));
        MoveInput[] inputs = [.. Enumerable.Range(0, 200).Select(i => new MoveInput(i * 3, 450, (i % 7) * 30 - 90, i % 40 == 0, i % 50 > 40))];

        PlayerState a = sim.Spawn(Vec3.Zero), b = sim.Spawn(Vec3.Zero);
        foreach (MoveInput input in inputs)
        {
            a = sim.Tick(a, input);
            b = sim.Tick(b, input);
        }

        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("scout", 400f, true)]
    [InlineData("heavy", 230f, false)]
    [InlineData("Medic", 320f, false)]
    [InlineData("demo", 280f, false)]
    public void Tf2ClassesCarryTheirSpeed(string cls, float speed, bool doubleJump)
    {
        MovementProfile p = MovementProfile.Tf2(cls);
        Assert.Equal(speed, p.MaxSpeed);
        Assert.Equal(doubleJump, p.DoubleJump);
        Assert.Equal(new Vec3(24, 24, 82), p.Maxs(false));
        Assert.Equal(new Vec3(24, 24, 62), p.Maxs(true));
        Assert.Throws<ArgumentException>(() => MovementProfile.Tf2("chef"));
    }

    [Fact]
    public async Task ACompiledRoomsFloorHoldsThePlayer()
    {
        BspData bsp = await PreviewTestMaps.RoomAsync(false, new PreviewTestMaps.TestEntity("info_player_teamspawn", [("origin", "256 256 16")]));
        (HullTraceWorld world, IReadOnlyList<string> warnings) = await HullTraceWorld.FromBspAsync(bsp, null);

        PlayerMoveSim sim = new(world, Soldier);
        PlayerState state = Run(sim, new PlayerState(new Vec3(256, 256, 100), Vec3.Zero, false), default, 66);

        Assert.Empty(warnings);
        Assert.True(state.OnGround);
        Assert.InRange(state.Origin.Z, 0f, 0.05f);
        Assert.True(world.Solids.Count >= 6);
    }

    /// <summary>
    /// A one-unit shell under a steep triangle, as a displacement's surface
    /// is collided: the triangle on top, the same plane a unit lower
    /// underneath, and a side through each edge square to the triangle.
    /// </summary>
    private static NavBrush TriangleShell(Vec3 a, Vec3 b, Vec3 c)
    {
        Vec3 n = Vec3.Cross(b - a, c - a).Normalise().Normalised;
        Vec3 centre = (a + b + c) * (1f / 3f);
        List<(Vec3 Normal, float Dist)> planes = [(n, Vec3.Dot(n, a)), (-n, -Vec3.Dot(n, a) + 1f)];
        foreach ((Vec3 p, Vec3 q) in new[] { (a, b), (b, c), (c, a) })
        {
            Vec3 side = Vec3.Cross(q - p, n).Normalise().Normalised;
            if (Vec3.Dot(side, centre - p) > 0)
            {
                side = -side;
            }

            planes.Add((side, Vec3.Dot(side, p)));
        }

        return NavBrush.FromPlanes(planes, Solid)!;
    }

    [Fact]
    public void ABoxComingDownBesideASteepTriangleStopsOnlyWhereItTouches()
    {
        // A triangle from a canyon rim's displacement, falling 173 units in
        // 64. Without bevels across its slanted edges a box coming down
        // beside it met the edges' extensions in mid-air, up to a hundred
        // units above the surface, and stood there on a ledge that is not
        // in the map. Every stop must be a real contact: lowered half a unit
        // further, the box overlaps the triangle. The sample columns sit off
        // the triangle's corner lines, so no box merely grazes a corner.
        NavBrush shell = TriangleShell(new Vec3(0, 0, 0), new Vec3(64, 0, -173), new Vec3(64, 64, -72));
        HullTraceWorld world = new([shell]);
        int hits = 0;
        for (int x = -36; x <= 100; x += 8)
        {
            for (int y = -36; y <= 100; y += 8)
            {
                HullTrace t = world.Trace(new Vec3(x, y, 100), new Vec3(x, y, -400), Mins, Maxs);
                if (!t.Hit)
                {
                    continue;
                }

                hits++;
                Assert.False(shell.Overlaps(At(t.End)), $"({x}, {y}) stopped inside the shell");
                Assert.True(shell.Overlaps(At(t.End - new Vec3(0, 0, 0.5f))), $"({x}, {y}) stopped short of the shell at {t.End.Z}");
            }
        }

        Assert.True(hits > 20);

        static NavBox At(Vec3 o) => new(o.X + Mins.X, o.Y + Mins.Y, o.Z + Mins.Z, o.X + Maxs.X, o.Y + Maxs.Y, o.Z + Maxs.Z);
    }

    [Fact]
    public void EdgeBevelsAreNewSupportingPlanesOnly()
    {
        // A box has none.
        Assert.Empty(HullTraceWorld.EdgeBevels(Box(0, 0, 0, 10, 10, 10)));

        // The prism z ≤ x (x ≤ 10, z ≥ 0, 0 ≤ y ≤ 10): its slope's edges
        // crossed with y give the slope's own normal, which the clip already
        // has, and the axial ones are bounding planes; only the slope's
        // reverse is new.
        float r = MathF.Sqrt(0.5f);
        NavBrush wedge = NavBrush.FromPlanes([
            (new Vec3(-r, 0, r), 0f), (new Vec3(1, 0, 0), 10f), (new Vec3(0, 0, -1), 0f),
            (new Vec3(0, 1, 0), 10f), (new Vec3(0, -1, 0), 0f)], Solid)!;
        List<(double X, double Y, double Z, double D)> planes = Planes(HullTraceWorld.EdgeBevels(wedge));
        Assert.DoesNotContain(planes, p => Near(p, -r, 0, r));
        Assert.DoesNotContain(planes, p => Math.Abs(p.X) > 0.999 || Math.Abs(p.Y) > 0.999 || Math.Abs(p.Z) > 0.999);
        Assert.Contains(planes, p => Near(p, r, 0, -r));

        // The tetrahedron x, y, z ≥ 0, x + y + z ≤ 10: the bevel across its
        // bottom edge, facing out along (1, 1, 0) at 10 / √2, is the one a
        // box needs; no two bevels face the same way.
        float third = 1f / MathF.Sqrt(3f);
        NavBrush tetra = NavBrush.FromPlanes([
            (new Vec3(-1, 0, 0), 0f), (new Vec3(0, -1, 0), 0f), (new Vec3(0, 0, -1), 0f),
            (new Vec3(third, third, third), 10f * third)], Solid)!;
        planes = Planes(HullTraceWorld.EdgeBevels(tetra));
        (double X, double Y, double Z, double D) diagonal = Assert.Single(planes, p => Near(p, r, r, 0));
        Assert.Equal(10 * r, diagonal.D, 4);
        for (int a = 0; a < planes.Count; a++)
        {
            Assert.All(planes.Skip(a + 1), p => Assert.False(Near(p, planes[a].X, planes[a].Y, planes[a].Z)));
        }

        static bool Near((double X, double Y, double Z, double D) p, double x, double y, double z) =>
            Math.Abs(p.X - x) < 1e-6 && Math.Abs(p.Y - y) < 1e-6 && Math.Abs(p.Z - z) < 1e-6;

        static List<(double X, double Y, double Z, double D)> Planes(double[] b) =>
            [.. Enumerable.Range(0, b.Length / 4).Select(i => (b[i * 4], b[(i * 4) + 1], b[(i * 4) + 2], b[(i * 4) + 3]))];
    }
}
