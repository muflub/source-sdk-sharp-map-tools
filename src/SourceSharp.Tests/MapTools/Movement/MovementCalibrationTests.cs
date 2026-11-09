//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Text;
using SourceSharp.MapTools.Movement;
using Xunit;

namespace SourceSharp.Tests.MapTools.Movement;

/// <summary>
/// Facts for <see cref="MovementCalibration"/>: the lanes, the map and
/// console script written from them, reading the game's log back, the
/// comparison, and the recorded TF2 run the simulator is held to.
/// </summary>
public sealed class MovementCalibrationTests
{
    private static readonly MovementProfile Soldier = MovementProfile.Tf2("soldier");
    private static readonly IReadOnlyList<CalibrationLane> Lanes = MovementCalibration.Lanes();

    /// <summary>The recorded TF2 soldier run, repo-relative.</summary>
    internal const string RecordedRun = "src/SourceSharp.Tests/Fixtures/movement-calibration/tf2_soldier.log";

    /// <summary>The recorded TF2 soldier run: the console lines a full calibration run printed.</summary>
    internal static string RecordedLog() => File.ReadAllText(RepoSourceFactAttribute.Find(RecordedRun)!);

    [RepoSourceFact(RecordedRun)]
    public void TheRecordedTf2RunMatchesTheSimulatorExceptWhereKnown()
    {
        // Every lane was played in TF2 (the SDK 2013 mod) as a soldier and
        // the marker and getpos lines of its console log kept. Each simulated
        // lane's final eye position must match the game's to two units, and
        // each probed peak to two. Only the lanes that carry a known
        // difference may differ, and they may also agree: where the slide
        // back from a steep slope ends varies between runs of the game
        // itself, so in one run a lane lands inside the tolerance and in the
        // next it does not.
        IReadOnlyList<CalibrationRow> rows = MovementCalibration.Compare(Lanes, MovementCalibration.ParseConsoleLog(RecordedLog()), Soldier);

        Assert.Equal(Lanes.Count, rows.Count);
        Assert.All(rows, r => Assert.True(r.Agrees || r.Known, r.Lane + ": " + r.Note));
        HashSet<string> mayDiffer = [.. Lanes.Where(l => l.KnownDifference is not null).Select(l => l.Name)];
        Assert.All(rows.Where(r => r.Known), r => Assert.Contains(r.Lane, mayDiffer));
        Assert.True(rows.Count(r => r.Agrees) >= Lanes.Count - mayDiffer.Count);
        Assert.All(rows.Where(r => r.Lane.StartsWith("rocket", StringComparison.Ordinal)), r => Assert.True(r.Agrees, r.Note));
    }

    [RepoSourceFact(RecordedRun)]
    public void ASlopeTooSteepIsClimbedByNeitherGameNorSimulator()
    {
        // The known difference on steep slopes is how far the slide back goes;
        // the outcome that matters, the player back on the floor in front of
        // the ramp (which starts at y = 128), is the same in both.
        IReadOnlyDictionary<string, CalibrationOutcome> game = MovementCalibration.ParseConsoleLog(RecordedLog());
        foreach (CalibrationLane lane in Lanes.Where(l => l.KnownDifference == MovementCalibration.SteepSlope))
        {
            CalibrationOutcome sim = MovementCalibration.Simulate(lane, Soldier);
            foreach (CalibrationOutcome o in new[] { game[lane.Name], sim })
            {
                Assert.Equal(Soldier.EyeHeight, o.FinalEye.Z, 0.5f);
                Assert.True(o.FinalEye.Y < 128 - Soldier.HalfWidth, lane.Name);
            }
        }
    }

    [Fact]
    public void LanesHaveUniqueNamesAndSolidBrushes()
    {
        Assert.Equal(Lanes.Count, Lanes.Select(l => l.Name).Distinct().Count());
        Assert.All(Lanes.SelectMany(l => l.Solids), s => Assert.NotNull(s.ToBrush()));
        Assert.Contains(Lanes, l => l.AlignTakeoff);
        Assert.Contains(Lanes, l => !l.Simulated);
    }

    [Fact]
    public void ARocketLaneCannotBeSimulated()
    {
        CalibrationLane rocket = Lanes.First(l => !l.Simulated);
        Assert.Throws<ArgumentException>(() => MovementCalibration.Simulate(rocket, Soldier));
    }

    [Fact]
    public void SolidsThatBoundNothingAreRejected()
    {
        CalibrationSolid open = new([(new Vec3(0, 0, 1), 0f)], "x");
        Assert.Throws<InvalidOperationException>(() => open.ToBrush());
    }

    [Fact]
    public void ARampRisesAtItsAngle()
    {
        NavBrush ramp = CalibrationSolid.RampY(0, 64, 100, 128, 45, "x").ToBrush();
        Assert.Equal(100 - 16, ramp.MinY, 2); // the slope meets the solid's base 16 below the floor
        Assert.Equal(228, ramp.MaxY, 2);
        Assert.Equal(128, ramp.MaxZ, 2);
    }

    [Fact]
    public async Task TheMapHoldsEveryLaneASkyShellAndTwoSpawns()
    {
        string vmf = MovementCalibration.WriteVmf(Lanes);
        KeyValuesDocument doc = await KeyValuesDocument.ParseAsync(vmf);
        KeyValuesNode world = doc.Roots.Single(r => r.Name == "world");

        Assert.Equal(Lanes.Sum(l => l.Solids.Count) + 6, world.FindAll("solid").Count());
        Assert.Equal(2, doc.Roots.Count(r => r.Name == "entity" && r.GetString("classname") == "info_player_teamspawn"));
        Assert.Contains(doc.Roots, r => r.GetString("classname") == "light_environment");
        Assert.Contains("TOOLS/TOOLSSKYBOX", vmf, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyLaneListStillWritesASealedMap()
    {
        string vmf = MovementCalibration.WriteVmf([]);
        Assert.Contains("info_player_teamspawn", vmf, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0f, 0f, 1f, 5f)]
    [InlineData(1f, 0f, 0f, -3f)]
    [InlineData(0f, -1f, 0f, 7f)]
    [InlineData(0f, -0.7660444f, 0.6427876f, -98f)]
    public void PlanePointsLieOnThePlaneAndWindOutward(float x, float y, float z, float d)
    {
        Vec3 n = new(x, y, z);
        (Vec3 p0, Vec3 p1, Vec3 p2) = MovementCalibration.PlanePoints(n, d);
        foreach (Vec3 p in new[] { p0, p1, p2 })
        {
            Assert.Equal(d, Vec3.Dot(n, p), 2);
        }

        Vec3 wound = Vec3.Cross(p0 - p1, p2 - p1).Normalise().Normalised;
        Assert.Equal(1f, Vec3.Dot(wound, n), 3);
    }

    [Fact]
    public void TheConsoleScriptChainsOneAliasIntoTheNext()
    {
        string script = MovementCalibration.WriteConsoleScript(Lanes, "soldier");
        string[] aliases = [.. script.Split('\n').Where(l => l.StartsWith("alias mc", StringComparison.Ordinal) && !l.StartsWith("alias mcstart", StringComparison.Ordinal))];

        for (int i = 0; i < aliases.Length - 1; i++)
        {
            Assert.StartsWith($"alias mc{i} \"", aliases[i], StringComparison.Ordinal);
            Assert.EndsWith($"; mc{i + 1}\"", aliases[i], StringComparison.Ordinal);
        }

        Assert.EndsWith("quit\"", aliases[^1], StringComparison.Ordinal);
        Assert.Contains("joinclass soldier", script, StringComparison.Ordinal);
        Assert.Equal(Lanes.Count, aliases.Count(a => a.Contains(MovementCalibration.LaneMarker, StringComparison.Ordinal)));
        Assert.Equal(Lanes.Count, aliases.Count(a => a.Contains(MovementCalibration.FinalMarker, StringComparison.Ordinal)));
        int probes = Lanes.Sum(l => l.Script.Where(p => p.Probe).Sum(p => p.Ticks));
        Assert.Equal(probes + Lanes.Count, aliases.Count(a => a.Contains("getpos", StringComparison.Ordinal)));
        Assert.Contains("+duck", script, StringComparison.Ordinal);
        Assert.Contains("+attack", script, StringComparison.Ordinal);
        Assert.Equal("mcstart", script.TrimEnd().Split('\n')[^1]);
    }

    [Fact]
    public void ALogIsReadLaneByLane()
    {
        string log = string.Join('\n',
            "noise",
            MovementCalibration.LaneMarker + " a",
            "setpos 0.000000 0.000000 68.000000;setang 0 90 0",
            "setpos 0.000000 2.000000 80.000000;setang 0 90 0",
            "setpos bad",
            "setpos 1 2;setang",
            "setpos x y z;setang",
            MovementCalibration.FinalMarker,
            "setpos 0.000000 9.000000 68.000000;setang 0 90 0",
            MovementCalibration.LaneMarker + " b",
            MovementCalibration.FinalMarker,
            "setpos 5 6 7",
            MovementCalibration.LaneMarker + " c",
            "setpos 1 1 1;setang 0 0 0");

        IReadOnlyDictionary<string, CalibrationOutcome> found = MovementCalibration.ParseConsoleLog(log);

        Assert.Equal(["a", "b"], found.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new Vec3(0, 9, 68), found["a"].FinalEye);
        Assert.Equal(80f, found["a"].ApexEyeZ);
        Assert.Equal(2, found["a"].Path.Count);
        Assert.True(float.IsNaN(found["b"].ApexEyeZ));
        Assert.Equal(new Vec3(5, 6, 7), found["b"].FinalEye);
    }

    [Fact]
    public void TheTakeoffIsTheLastSampleBeforeTheEyeRises()
    {
        CalibrationOutcome jump = new(Vec3.Zero, 80, [new(0, 0, 68), new(0, 5, 68.2f), new(0, 9, 70), new(0, 12, 75)]);
        CalibrationOutcome flat = new(Vec3.Zero, 68, [new(0, 0, 68), new(0, 5, 68)]);

        Assert.Equal(new Vec3(0, 5, 68.2f), jump.Takeoff);
        Assert.Null(flat.Takeoff);
    }

    [Fact]
    public void ALaunchSpeedComesFromThePeak()
    {
        // Standing: the feet rose as far as the eye. Crouched: the feet
        // also came up the 20 of the hull difference and the eye dropped 23.
        Assert.Equal(MathF.Sqrt(2 * 800 * 300f), MovementCalibration.LaunchSpeed(300, false, Soldier), 2);
        Assert.Equal(MathF.Sqrt(2 * 800 * 303f), MovementCalibration.LaunchSpeed(300, true, Soldier), 2);
        Assert.Equal(0f, MovementCalibration.LaunchSpeed(-5, false, Soldier));
    }

    [Fact]
    public void TheComparisonSaysWhereAndWhy()
    {
        CalibrationLane step = Lanes.First(l => l.Name == "step_16");
        CalibrationLane steep = Lanes.First(l => l.Name == "slope_50");
        CalibrationLane rocket = Lanes.First(l => l.Name == "rocket_jump");
        CalibrationLane crouchRocket = Lanes.First(l => l.Name == "rocket_cjump");
        CalibrationLane apex = Lanes.First(l => l.Name == "apex_jump");
        CalibrationOutcome stepSim = MovementCalibration.Simulate(step, Soldier);
        CalibrationOutcome apexSim = MovementCalibration.Simulate(apex, Soldier);
        Dictionary<string, CalibrationOutcome> game = new()
        {
            ["step_16"] = stepSim,
            ["slope_50"] = new(new Vec3(steep.Start.X, 50, 68), float.NaN, []),
            ["rocket_jump"] = new(Vec3.Zero, 68 + 300, [new(0, 0, 68), new(0, 0, 368)]),
            ["rocket_cjump"] = new(Vec3.Zero, 68 + 10, [new(0, 0, 68)]),
            ["apex_jump"] = new(apexSim.FinalEye, apexSim.ApexEyeZ + 5, apexSim.Path),
        };

        IReadOnlyList<CalibrationRow> rows = MovementCalibration.Compare([step, steep, rocket, crouchRocket, apex, Lanes[1]], game, Soldier);

        Assert.True(rows[0].Agrees);
        Assert.False(rows[1].Agrees);
        Assert.True(rows[1].Known);
        Assert.Contains("known:", rows[1].Note, StringComparison.Ordinal);
        Assert.True(rows[2].Agrees); // about 693 u/s: inside the rocket jump's range
        Assert.False(rows[3].Agrees); // far too low for a rocket jump
        Assert.False(rows[4].Agrees); // the peak is 5 off
        Assert.Contains("peak by 5", rows[4].Note, StringComparison.Ordinal);
        Assert.Equal("not in the log", rows[5].Note);
        Assert.Null(rows[5].Game);

        IReadOnlyList<CalibrationRow> missingRocket = MovementCalibration.Compare([rocket], new Dictionary<string, CalibrationOutcome>(), Soldier);
        Assert.Equal("not in the log", missingRocket[0].Note);

        string table = MovementCalibration.Format(rows);
        Assert.Contains("known", table, StringComparison.Ordinal);
        Assert.Contains("NO", table, StringComparison.Ordinal);
        Assert.Contains("2 of 6 lanes agree, 1 differ as known", table, StringComparison.Ordinal);
        Assert.DoesNotContain("as known", MovementCalibration.Format([rows[0]]), StringComparison.Ordinal);
    }

    [Fact]
    public void ARunUpIsMatchedToTheGamesTakeoff()
    {
        // A gap lane's game run left the ground further along than the
        // script's run-up reaches in the simulator: the comparison lengthens
        // the simulator's run-up to match, and says so.
        CalibrationLane gap = Lanes.First(l => l.AlignTakeoff);
        int ticks = MovementCalibration.RunUpMatching(gap, Soldier, 150);
        CalibrationLane longer = gap with { Script = [gap.Script[0] with { Ticks = ticks, Probe = true }] };

        // Running at 240 units a second the player covers 3.6 a tick, so the
        // nearest tick is within 1.8 of the target.
        Assert.InRange(MovementCalibration.Simulate(longer, Soldier).Path[^1].Y, 148.2f, 151.8f);
        Assert.Equal(1, MovementCalibration.RunUpMatching(gap, Soldier, -1000));

        CalibrationOutcome sim = MovementCalibration.Simulate(gap, Soldier);
        Vec3 takeoff = sim.Takeoff!.Value;
        CalibrationOutcome moved = sim with { Path = [.. sim.Path.Select(p => new Vec3(p.X, p.Y + 3.6f, p.Z))] };
        CalibrationRow row = MovementCalibration.Compare([gap], new Dictionary<string, CalibrationOutcome> { [gap.Name] = moved }, Soldier)[0];
        Assert.Contains("run-up", row.Note, StringComparison.Ordinal);
        Assert.True(takeoff.Y > 0);
    }
}
