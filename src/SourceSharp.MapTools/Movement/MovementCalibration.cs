//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Numerics;

namespace SourceSharp.MapTools.Movement;

/// <summary>
/// One stretch of a calibration lane's input: buttons held for a number of
/// ticks.
/// </summary>
/// <param name="Ticks">How long the buttons are held, in ticks.</param>
/// <param name="Forward">Whether forward is held.</param>
/// <param name="Jump">Whether jump is held.</param>
/// <param name="Duck">Whether crouch is held.</param>
/// <param name="Attack">Whether fire is held (the game only; the simulator has no weapons).</param>
/// <param name="Probe">Whether the eye position is recorded every tick of the stretch.</param>
public readonly record struct CalibrationPhase(
    int Ticks, bool Forward = false, bool Jump = false, bool Duck = false, bool Attack = false, bool Probe = false);

/// <summary>A convex solid of a calibration lane, as outward planes (inside is <c>n·x ≤ d</c>).</summary>
/// <param name="Planes">The bounding planes.</param>
/// <param name="Material">The material its faces get in the map.</param>
public sealed record CalibrationSolid(IReadOnlyList<(Vec3 Normal, float Dist)> Planes, string Material)
{
    /// <summary>An axis-aligned box.</summary>
    /// <param name="mins">The low corner.</param>
    /// <param name="maxs">The high corner.</param>
    /// <param name="material">Its material.</param>
    /// <returns>The solid.</returns>
    public static CalibrationSolid Box(Vec3 mins, Vec3 maxs, string material) => new(
        [
            (new Vec3(1, 0, 0), maxs.X), (new Vec3(-1, 0, 0), -mins.X),
            (new Vec3(0, 1, 0), maxs.Y), (new Vec3(0, -1, 0), -mins.Y),
            (new Vec3(0, 0, 1), maxs.Z), (new Vec3(0, 0, -1), -mins.Z),
        ],
        material);

    /// <summary>
    /// A ramp rising along +Y from height 0 at <paramref name="y0"/> to
    /// <paramref name="height"/>, at <paramref name="degrees"/> above level,
    /// between two X bounds, standing on z = -16.
    /// </summary>
    /// <param name="x0">The low X bound.</param>
    /// <param name="x1">The high X bound.</param>
    /// <param name="y0">Where its slope leaves the floor.</param>
    /// <param name="height">The height its slope ends at.</param>
    /// <param name="degrees">Its angle.</param>
    /// <param name="material">Its material.</param>
    /// <returns>The solid.</returns>
    public static CalibrationSolid RampY(float x0, float x1, float y0, float height, float degrees, string material)
    {
        float a = degrees * (MathF.PI / 180f);
        float s = DetMathF.Sin(a), c = DetMathF.Cos(a);
        float run = height * c / s;
        return new CalibrationSolid(
            [
                (new Vec3(0, -s, c), -s * y0), (new Vec3(0, 0, -1), 16f),
                (new Vec3(1, 0, 0), x1), (new Vec3(-1, 0, 0), -x0), (new Vec3(0, 1, 0), y0 + run),
            ],
            material);
    }

    /// <summary>The solid as a collision brush.</summary>
    /// <returns>The brush.</returns>
    public NavBrush ToBrush() => NavBrush.FromPlanes(Planes, 1)
        ?? throw new InvalidOperationException("a calibration solid's planes bound nothing");
}

/// <summary>
/// One calibration lane: a corridor with one obstacle, a start, and the
/// input a player gives in it.
/// </summary>
/// <param name="Name">The lane's name, e.g. <c>step_18</c>.</param>
/// <param name="Description">What the lane measures.</param>
/// <param name="Start">Where the player's feet are put, in map coordinates.</param>
/// <param name="Pitch">The view pitch, degrees (positive looks down).</param>
/// <param name="Solids">The lane's floor, walls and obstacle.</param>
/// <param name="Script">The input.</param>
/// <param name="Simulated">Whether the simulator can play it: false for lanes that need a weapon.</param>
/// <param name="AlignTakeoff">
/// Whether the simulator's run-up (the script's first stretch) is lengthened
/// or shortened so that its jump leaves the ground where the game's probes
/// say the game's did. A held button in the game lasts frames, not ticks,
/// so a run-up's length in the game is only near the script's; for a lane
/// whose outcome hangs on where the jump starts (a gap) this takes that
/// difference out.
/// </param>
/// <param name="KnownDifference">
/// Why the simulator is known not to match the game on this lane, or null.
/// A lane with a known difference that disagrees is reported as known
/// rather than as a failure; one that agrees is reported as agreeing, so a
/// note that no longer holds shows.
/// </param>
public sealed record CalibrationLane(
    string Name, string Description, Vec3 Start, float Pitch,
    IReadOnlyList<CalibrationSolid> Solids, IReadOnlyList<CalibrationPhase> Script, bool Simulated = true, bool AlignTakeoff = false,
    string? KnownDifference = null);

/// <summary>Where a lane's player ended, how high the eye went while probed, and the probed path.</summary>
/// <param name="FinalEye">The eye position after the lane's input and the settle.</param>
/// <param name="ApexEyeZ">The highest eye position probed, or NaN when nothing was probed.</param>
/// <param name="Path">The probed eye positions, in order.</param>
public sealed record CalibrationOutcome(Vec3 FinalEye, float ApexEyeZ, IReadOnlyList<Vec3> Path)
{
    /// <summary>
    /// Where the probed path left the ground: the last sample before the eye
    /// first rose more than half a unit above where it started, or null when
    /// it never did.
    /// </summary>
    public Vec3? Takeoff
    {
        get
        {
            for (int i = 1; i < Path.Count; i++)
            {
                if (Path[i].Z > Path[0].Z + 0.5f)
                {
                    return Path[i - 1];
                }
            }

            return null;
        }
    }
}

/// <summary>One lane's game and simulator outcomes side by side.</summary>
/// <param name="Lane">The lane.</param>
/// <param name="Game">What the game did, or null when the log lacks the lane.</param>
/// <param name="Sim">What the simulator did, or null for a lane it cannot play.</param>
/// <param name="Agrees">Whether they agree within the tolerances (or, for a measurement lane, whether the measurement is in range).</param>
/// <param name="Note">A short explanation of the comparison.</param>
/// <param name="Known">Whether a disagreement is the lane's known difference.</param>
public sealed record CalibrationRow(string Lane, CalibrationOutcome? Game, CalibrationOutcome? Sim, bool Agrees, string Note, bool Known = false);

/// <summary>
/// The movement calibration: a set of lanes, each one obstacle at a
/// measured size (a step, a slope, a ledge, a tunnel, a gap) or a launch to
/// measure, that is played both in the real game and in
/// <see cref="PlayerMoveSim"/>, and the two outcomes compared.
/// </summary>
/// <remarks>
/// <para>
/// The game is driven from its console: a script of aliases that put the
/// player at a lane's start, hold buttons for a number of frames and print
/// the eye position with <c>getpos</c>. Frames are not ticks, so how long a
/// button is held in the game is only near the script's count. The lanes
/// are built so that this does not matter: every lane ends at a wall, and a
/// player either stops at the obstacle or runs on to the end wall, so the
/// final position is exact either way. Only the probed apex of a jump is a
/// trajectory measurement, and an apex is flat, so a frame either side
/// changes it by a fraction of a unit.
/// </para>
/// <para>
/// <c>getpos</c> reports the eye, not the feet, so outcomes are compared as
/// eye positions: the simulator's feet plus the profile's standing or
/// crouched eye height.
/// </para>
/// </remarks>
public static class MovementCalibration
{
    /// <summary>The ticks of no input after a player is put at a lane's start, so it lands and stops.</summary>
    public const int SettleTicks = 45;

    /// <summary>The ticks of no input after a lane's script, before the final position is read.</summary>
    public const int EndTicks = 60;

    /// <summary>
    /// The forward input of a held forward key: the game's default
    /// <c>cl_forwardspeed</c>, which the movement clamps to the class's
    /// speed.
    /// </summary>
    public const float ForwardMove = 450f;

    /// <summary>How far apart lanes are along X.</summary>
    public const float LaneSpacing = 256f;

    /// <summary>Half a lane's inside width.</summary>
    public const float HalfWidth = 96f;

    /// <summary>How far a final eye position may differ, per axis, and still agree.</summary>
    public const float FinalTolerance = 2f;

    /// <summary>How far a probed apex may differ and still agree.</summary>
    public const float ApexTolerance = 2f;

    /// <summary>The known difference on a slope too steep to stand on.</summary>
    public const string SteepSlope =
        "both creep about 20 units up a slope too steep to stand on and slide back to the floor in front of it, " +
        "but the game stops climbing near 24 where the simulator keeps inching up while forward is held, so the " +
        "slide back after letting go starts higher and ends a few units further back; where it ends also varies by " +
        "about 3 units between runs of the game itself";

    private const string Floor = "nature/dirtground003";
    private const string Wall = "concrete/wall011";
    private const string Obstacle = "wood/wood_wall004";

    /// <summary>
    /// The standard lanes, laid side by side along X, each running along +Y
    /// from its start at y = 0.
    /// </summary>
    /// <returns>The lanes.</returns>
    public static IReadOnlyList<CalibrationLane> Lanes()
    {
        List<CalibrationLane> lanes = [];
        float x = 0;

        CalibrationLane Lane(string name, string description, float end, IEnumerable<CalibrationSolid> obstacles,
            IReadOnlyList<CalibrationPhase> script, bool floor = true, bool simulated = true, float pitch = 0, bool align = false,
            string? known = null)
        {
            List<CalibrationSolid> solids = [.. Walls(x, end)];
            if (floor)
            {
                solids.Add(CalibrationSolid.Box(new Vec3(x - HalfWidth, -64, -16), new Vec3(x + HalfWidth, end, 0), Floor));
            }

            solids.AddRange(obstacles);
            CalibrationLane lane = new(name, description, new Vec3(x, 0, 4), pitch, solids, script, simulated, align, known);
            lanes.Add(lane);
            x += LaneSpacing;
            return lane;
        }

        CalibrationSolid Block(float y0, float y1, float z0, float z1, string material = Obstacle) =>
            CalibrationSolid.Box(new Vec3(x - HalfWidth, y0, z0), new Vec3(x + HalfWidth, y1, z1), material);

        // Steps: walking into a ledge of each height. The step-up height is
        // 18; at 19 the player stops.
        foreach (int h in new[] { 16, 18, 19, 20, 24 })
        {
            Lane($"step_{h}", $"walking into a {h}-unit step", 640, [Block(128, 640, 0, h)], [new(240, Forward: true)]);
        }

        // Slopes: walking up a ramp. A floor is a surface whose normal's z is
        // at least 0.7, a slope of about 45.6 degrees.
        foreach (int a in new[] { 40, 45, 46, 50 })
        {
            float run = 128f * DetMathF.Cos(a * (MathF.PI / 180f)) / DetMathF.Sin(a * (MathF.PI / 180f));
            Lane($"slope_{a}", $"walking up a {a}-degree ramp", 640,
                [CalibrationSolid.RampY(x - HalfWidth, x + HalfWidth, 128, 128, a, Obstacle), Block(128 + run, 640, -16, 128)],
                [new(300, Forward: true)],
                known: a > 45 ? SteepSlope : null);
        }

        // Ledges reached by a running jump from close in, without and with a
        // crouch.
        foreach (int h in new[] { 40, 44, 48, 52, 56 })
        {
            Lane($"jump_{h}", $"jumping onto a {h}-unit ledge", 640, [Block(64, 640, 0, h)],
                [new(10, Forward: true), new(30, Forward: true, Jump: true), new(400, Forward: true)]);
        }

        foreach (int h in new[] { 60, 64, 68, 72, 76, 80 })
        {
            Lane($"cjump_{h}", $"crouch-jumping onto a {h}-unit ledge", 640, [Block(64, 640, 0, h)],
                [new(10, Forward: true), new(40, Forward: true, Jump: true, Duck: true), new(400, Forward: true)]);
        }

        // Tunnels: walking crouched under a ceiling at each height (the
        // crouched hull is 62), and standing under one near the standing
        // hull's 82.
        foreach (int h in new[] { 61, 62, 63, 64 })
        {
            Lane($"tunnel_{h}", $"crouch-walking into a {h}-unit tunnel", 448, [Block(96, 448, h, h + 32)],
                [new(600, Forward: true, Duck: true), new(30, Duck: true)]);
        }

        foreach (int h in new[] { 82, 83 })
        {
            Lane($"ceiling_{h}", $"walking under an {h}-unit ceiling", 448, [Block(96, 448, h, h + 32)], [new(200, Forward: true)]);
        }

        // Gaps: a running jump from a run-up that ends at y = 192 over a pit
        // 128 deep. A player who falls runs on to the pit's far wall.
        foreach (int w in new[] { 96, 144, 160, 176, 208 })
        {
            const float edge = 192;
            Lane($"gap_{w}", $"a running jump over a {w}-unit gap", 640,
                [
                    Block(-64, edge, -16, 0, Floor), Block(edge, edge + w, -144, -128, Floor), Block(edge + w, 640, -144, 0, Floor),
                ],
                [new(45, Forward: true, Probe: true), new(30, Forward: true, Jump: true, Probe: true), new(400, Forward: true)],
                floor: false, align: true);
        }

        // Jump peaks, probed every tick: a standing jump and a crouch jump.
        // Every press lasts at least three ticks: the game turns a frame's
        // buttons into the next tick's input, and a press held for a single
        // frame can fall between two ticks and be lost.
        Lane("apex_jump", "a standing jump's peak", 448, [], [new(3, Jump: true, Probe: true), new(70, Probe: true)]);
        Lane("apex_cjump", "a standing crouch jump's peak", 448, [], [new(3, Jump: true, Duck: true, Probe: true), new(70, Duck: true, Probe: true)]);

        // Rocket jumps, looking straight down: measured in the game only.
        Lane("rocket_cjump", "a crouched rocket jump's launch, looking down", 448, [],
            [new(3, Jump: true, Duck: true, Probe: true), new(3, Jump: true, Duck: true, Attack: true, Probe: true), new(110, Duck: true, Probe: true), new(120, Duck: true)],
            simulated: false, pitch: 89);
        Lane("rocket_jump", "a standing rocket jump's launch, looking down", 448, [],
            [new(3, Jump: true, Probe: true), new(3, Jump: true, Attack: true, Probe: true), new(110, Probe: true), new(120)],
            simulated: false, pitch: 89);

        return lanes;
    }

    /// <summary>A lane's side, start and end walls, tall enough to keep a rocket jump in.</summary>
    private static IEnumerable<CalibrationSolid> Walls(float x, float end)
    {
        yield return CalibrationSolid.Box(new Vec3(x - HalfWidth - 16, -80, -160), new Vec3(x - HalfWidth, end + 16, 768), Wall);
        yield return CalibrationSolid.Box(new Vec3(x + HalfWidth, -80, -160), new Vec3(x + HalfWidth + 16, end + 16, 768), Wall);
        yield return CalibrationSolid.Box(new Vec3(x - HalfWidth, -80, -160), new Vec3(x + HalfWidth, -64, 768), Wall);
        yield return CalibrationSolid.Box(new Vec3(x - HalfWidth, end, -160), new Vec3(x + HalfWidth, end + 16, 768), Wall);
    }

    /// <summary>The eye height for a state: standing or crouched.</summary>
    private static float Eye(MovementProfile profile, bool ducked) => ducked ? profile.DuckEyeHeight : profile.EyeHeight;

    /// <summary>
    /// Plays a lane in the simulator: the settle, the script, and the end
    /// settle, facing +Y.
    /// </summary>
    /// <param name="lane">The lane.</param>
    /// <param name="profile">The movement numbers.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="ArgumentException">The lane needs a weapon.</exception>
    public static CalibrationOutcome Simulate(CalibrationLane lane, MovementProfile profile)
    {
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentNullException.ThrowIfNull(profile);
        if (!lane.Simulated)
        {
            throw new ArgumentException($"lane \"{lane.Name}\" needs a weapon, which the simulator does not have", nameof(lane));
        }

        PlayerMoveSim sim = new(new HullTraceWorld([.. lane.Solids.Select(s => s.ToBrush())]), profile);
        PlayerState state = sim.Spawn(lane.Start);
        for (int t = 0; t < SettleTicks; t++)
        {
            state = sim.Tick(state, new MoveInput(90, 0, 0, false, false));
        }

        float apex = float.NaN;
        List<Vec3> path = [];
        bool duck = false;
        foreach (CalibrationPhase phase in lane.Script)
        {
            for (int t = 0; t < phase.Ticks; t++)
            {
                state = sim.Tick(state, new MoveInput(90, phase.Forward ? ForwardMove : 0, 0, phase.Jump, phase.Duck));
                if (phase.Probe)
                {
                    Vec3 eye = state.Origin + new Vec3(0, 0, Eye(profile, state.Ducked));
                    apex = path.Count == 0 ? eye.Z : Math.Max(apex, eye.Z);
                    path.Add(eye);
                }
            }

            duck = phase.Duck;
        }

        for (int t = 0; t < EndTicks; t++)
        {
            state = sim.Tick(state, new MoveInput(90, 0, 0, false, duck));
        }

        return new CalibrationOutcome(state.Origin + new Vec3(0, 0, Eye(profile, state.Ducked)), apex, path);
    }

    /// <summary>
    /// Writes the calibration map: every lane, a sky shell around them, a
    /// sun, and spawns for both teams at the first lane's start.
    /// </summary>
    /// <param name="lanes">The lanes.</param>
    /// <returns>The VMF text.</returns>
    public static string WriteVmf(IReadOnlyList<CalibrationLane> lanes)
    {
        ArgumentNullException.ThrowIfNull(lanes);
        List<CalibrationSolid> world = [.. lanes.SelectMany(l => l.Solids)];
        float x0 = -LaneSpacing, x1 = (lanes.Count * LaneSpacing) + 16;
        const float y0 = -256, y1 = 1024, z0 = -320, z1 = 1280, t = 16;
        const string sky = "tools/toolsskybox";
        world.Add(CalibrationSolid.Box(new Vec3(x0 - t, y0 - t, z1), new Vec3(x1 + t, y1 + t, z1 + t), sky));
        world.Add(CalibrationSolid.Box(new Vec3(x0 - t, y0 - t, z0 - t), new Vec3(x1 + t, y1 + t, z0), "tools/toolsnodraw"));
        world.Add(CalibrationSolid.Box(new Vec3(x0 - t, y0 - t, z0), new Vec3(x0, y1 + t, z1), sky));
        world.Add(CalibrationSolid.Box(new Vec3(x1, y0 - t, z0), new Vec3(x1 + t, y1 + t, z1), sky));
        world.Add(CalibrationSolid.Box(new Vec3(x0, y0 - t, z0), new Vec3(x1, y0, z1), sky));
        world.Add(CalibrationSolid.Box(new Vec3(x0, y1, z0), new Vec3(x1, y1 + t, z1), sky));

        StringBuilder text = new();
        int id = 0;
        text.Append("versioninfo\n{\n\t\"editorversion\" \"400\"\n\t\"mapversion\" \"1\"\n\t\"formatversion\" \"100\"\n\t\"prefab\" \"0\"\n}\n");
        text.Append("world\n{\n").Append(Inv($"\t\"id\" \"{++id}\"\n")).Append("\t\"mapversion\" \"1\"\n\t\"classname\" \"worldspawn\"\n\t\"skyname\" \"sky_badlands_01\"\n");
        foreach (CalibrationSolid solid in world)
        {
            text.Append(Inv($"\tsolid\n\t{{\n\t\t\"id\" \"{++id}\"\n"));
            foreach ((Vec3 n, float d) in solid.Planes)
            {
                (Vec3 p0, Vec3 p1, Vec3 p2) = PlanePoints(n, d);
                (string u, string v) = TextureAxes(n);
                text.Append(Inv($"\t\tside\n\t\t{{\n\t\t\t\"id\" \"{++id}\"\n\t\t\t\"plane\" \"{P(p0)} {P(p1)} {P(p2)}\"\n"))
                    .Append(Inv($"\t\t\t\"material\" \"{solid.Material.ToUpperInvariant()}\"\n\t\t\t\"uaxis\" \"{u}\"\n\t\t\t\"vaxis\" \"{v}\"\n"))
                    .Append("\t\t\t\"rotation\" \"0\"\n\t\t\t\"lightmapscale\" \"16\"\n\t\t\t\"smoothing_groups\" \"0\"\n\t\t}\n");
            }

            text.Append("\t}\n");
        }

        text.Append("}\n");
        void Entity(params (string Key, string Value)[] keys)
        {
            text.Append(Inv($"entity\n{{\n\t\"id\" \"{++id}\"\n"));
            foreach ((string k, string v) in keys)
            {
                text.Append(Inv($"\t\"{k}\" \"{v}\"\n"));
            }

            text.Append("}\n");
        }

        Entity(("classname", "light_environment"), ("origin", "0 0 900"), ("angles", "-60 45 0"), ("pitch", "-60"),
            ("_light", "255 236 210 400"), ("_ambient", "150 160 190 90"), ("_lightHDR", "-1 -1 -1 1"), ("_ambientHDR", "-1 -1 -1 1"));
        Vec3 spawn = lanes.Count > 0 ? lanes[0].Start : Vec3.Zero;
        foreach (int team in new[] { 2, 3 })
        {
            Entity(("classname", "info_player_teamspawn"), ("origin", Inv($"{spawn.X:R} {spawn.Y:R} {spawn.Z + 12:R}")), ("angles", "0 90 0"),
                ("TeamNum", team.ToString(CultureInfo.InvariantCulture)));
        }

        return text.ToString();
    }

    /// <summary>
    /// Three points on a plane, wound so the map compiler's normal
    /// (<c>(p0 - p1) × (p2 - p1)</c>) faces out of the solid. They need not
    /// lie on the face itself: the compiler builds each face from the planes.
    /// </summary>
    internal static (Vec3 P0, Vec3 P1, Vec3 P2) PlanePoints(Vec3 n, float d)
    {
        Vec3 axis = MathF.Abs(n.Z) < 0.9f ? new Vec3(0, 0, 1) : new Vec3(1, 0, 0);
        Vec3 t1 = Vec3.Cross(n, axis).Normalise().Normalised;
        Vec3 t2 = Vec3.Cross(n, t1);
        Vec3 c = n * d;
        return (c + (t1 * 256f), c, c + (t2 * 256f));
    }

    private static (string U, string V) TextureAxes(Vec3 n)
    {
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        return az >= ax && az >= ay ? ("[1 0 0 0] 0.25", "[0 -1 0 0] 0.25")
            : ax >= ay ? ("[0 1 0 0] 0.25", "[0 0 -1 0] 0.25")
            : ("[1 0 0 0] 0.25", "[0 0 -1 0] 0.25");
    }

    private static string P(Vec3 p) => Inv($"({p.X:R} {p.Y:R} {p.Z:R})");

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>The console command that names a lane in the log.</summary>
    public const string LaneMarker = "MOVECAL_LANE";

    /// <summary>The console command that says the next position is a lane's final one.</summary>
    public const string FinalMarker = "MOVECAL_FINAL";

    /// <summary>The console command that says the run has finished.</summary>
    public const string DoneMarker = "MOVECAL_DONE";

    /// <summary>
    /// Writes the game's console script: settings, then one chain of
    /// aliases per lane, each alias holding its buttons, waiting and calling
    /// the next as its last command (a <c>wait</c> inside a nested alias does
    /// not hold, and one console line has a length limit). <c>exec</c> the
    /// file once the map has loaded; it joins a team as the class, plays
    /// every lane and quits.
    /// <para>
    /// <c>wait</c> counts frames, so the game must run at one frame a tick:
    /// start it with <c>+fps_max 66</c> on its command line. The script cannot
    /// set it, because the frame cap cannot change while connected; left at
    /// its default the game runs several frames a tick and every hold is
    /// that many times shorter than the script says.
    /// </para>
    /// </summary>
    /// <param name="lanes">The lanes.</param>
    /// <param name="playerClass">The TF2 class to play.</param>
    /// <returns>The script.</returns>
    public static string WriteConsoleScript(IReadOnlyList<CalibrationLane> lanes, string playerClass)
    {
        ArgumentNullException.ThrowIfNull(lanes);
        ArgumentNullException.ThrowIfNull(playerClass);
        List<string> lines =
        [
            "cl_hud_playerclass_playermodel_showed_confirm_dialog 1", "sv_allow_wait_command 1", "sv_cheats 1",
            "tf_arena_use_queue 0", "mp_enableroundwaittime 0", "mp_teams_unbalance_limit 0", "mp_autoteambalance 0",
            "cl_updaterate 66", "cl_cmdrate 66", "cl_interp 0", "cl_interp_ratio 1", "cl_drawhud 0",
        ];

        int alias = 0;
        void Alias(string body)
        {
            lines.Add(Inv($"alias mc{alias} \"{body}; mc{alias + 1}\""));
            alias++;
        }

        lines.Add(Inv($"alias mcstart \"wait 300; jointeam red; wait 60; joinclass {playerClass}; wait 300; mp_waitingforplayers_cancel 1; wait 900; mc0\""));
        foreach (CalibrationLane lane in lanes)
        {
            Vec3 s = lane.Start;
            Alias(Inv($"echo {LaneMarker} {lane.Name}; -forward; -jump; -duck; -attack; setpos {s.X:R} {s.Y:R} {s.Z:R}; setang {lane.Pitch:R} 90 0; wait {SettleTicks}"));
            foreach (CalibrationPhase phase in lane.Script)
            {
                string buttons = Inv($"{(phase.Forward ? '+' : '-')}forward; {(phase.Jump ? '+' : '-')}jump; {(phase.Duck ? '+' : '-')}duck; {(phase.Attack ? '+' : '-')}attack");
                if (!phase.Probe)
                {
                    Alias(Inv($"{buttons}; wait {phase.Ticks}"));
                    continue;
                }

                for (int t = 0; t < phase.Ticks; t++)
                {
                    Alias(t == 0 ? Inv($"{buttons}; getpos; wait 1") : "getpos; wait 1");
                }
            }

            bool duck = lane.Script.Count > 0 && lane.Script[^1].Duck;
            Alias(Inv($"-forward; -jump; {(duck ? '+' : '-')}duck; -attack; wait {EndTicks}; echo {FinalMarker}; getpos"));
        }

        lines.Add(Inv($"alias mc{alias} \"-duck; echo {DoneMarker}; quit\""));
        lines.Add("mcstart");
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>
    /// Reads a calibration run's lane outcomes from the game's console log
    /// (<c>-condebug</c>): each lane's probed eye positions and its final
    /// one. A lane whose final position is missing is left out.
    /// </summary>
    /// <param name="log">The console log text.</param>
    /// <returns>The outcomes by lane name.</returns>
    public static IReadOnlyDictionary<string, CalibrationOutcome> ParseConsoleLog(string log)
    {
        ArgumentNullException.ThrowIfNull(log);
        Dictionary<string, CalibrationOutcome> found = new(StringComparer.Ordinal);
        string? lane = null;
        bool final = false;
        List<Vec3> path = [];
        foreach (string raw in log.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith(LaneMarker + " ", StringComparison.Ordinal))
            {
                lane = line[(LaneMarker.Length + 1)..].Trim();
                final = false;
                path = [];
            }
            else if (line == FinalMarker)
            {
                final = true;
            }
            else if (lane is not null && TryGetPos(line, out Vec3 eye))
            {
                if (final)
                {
                    found[lane] = new CalibrationOutcome(eye, path.Count == 0 ? float.NaN : path.Max(p => p.Z), path);
                    lane = null;
                }
                else
                {
                    path.Add(eye);
                }
            }
        }

        return found;
    }

    /// <summary>Reads <c>getpos</c>'s output: <c>setpos x y z;setang p y r</c>.</summary>
    internal static bool TryGetPos(string line, out Vec3 eye)
    {
        eye = default;
        if (!line.StartsWith("setpos ", StringComparison.Ordinal))
        {
            return false;
        }

        int end = line.IndexOf(';', StringComparison.Ordinal);
        string[] parts = line[7..(end < 0 ? line.Length : end)].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
        {
            return false;
        }

        eye = new Vec3(x, y, z);
        return true;
    }

    /// <summary>
    /// Compares the game's outcomes with the simulator's, lane by lane. A
    /// simulated lane agrees when the final eye positions are within
    /// <see cref="FinalTolerance"/> on every axis and, when both probed a
    /// peak, the peaks are within <see cref="ApexTolerance"/>. A rocket lane
    /// agrees when the launch speed its peak implies is inside
    /// <see cref="LaunchKind.RocketJump"/>'s range.
    /// </summary>
    /// <param name="lanes">The lanes.</param>
    /// <param name="game">The game's outcomes by lane name.</param>
    /// <param name="profile">The movement numbers for the simulator.</param>
    /// <returns>One row per lane.</returns>
    public static IReadOnlyList<CalibrationRow> Compare(
        IReadOnlyList<CalibrationLane> lanes, IReadOnlyDictionary<string, CalibrationOutcome> game, MovementProfile profile)
    {
        ArgumentNullException.ThrowIfNull(lanes);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(profile);
        List<CalibrationRow> rows = [];
        foreach (CalibrationLane lane in lanes)
        {
            CalibrationOutcome? g = game.TryGetValue(lane.Name, out CalibrationOutcome found) ? found : null;
            if (!lane.Simulated)
            {
                if (g is not { } m || float.IsNaN(m.ApexEyeZ))
                {
                    rows.Add(new CalibrationRow(lane.Name, g, null, false, "not in the log"));
                    continue;
                }

                // Every lane's floor is at z = 0, where a standing player's eye is the eye height.
                bool crouched = lane.Script.Count > 0 && lane.Script[^1].Duck;
                float speed = LaunchSpeed(m.ApexEyeZ - profile.EyeHeight, crouched, profile);
                LaunchKind kind = LaunchKind.RocketJump;
                rows.Add(new CalibrationRow(lane.Name, g, null, speed >= kind.MinSpeed && speed <= kind.MaxSpeed,
                    Inv($"launch about {speed:0} u/s upward; {kind.Name} samples {kind.MinSpeed:0}-{kind.MaxSpeed:0}")));
                continue;
            }

            if (g is not { } game1)
            {
                rows.Add(new CalibrationRow(lane.Name, null, Simulate(lane, profile), false, "not in the log"));
                continue;
            }

            string aligned = string.Empty;
            CalibrationLane played = lane;
            if (lane.AlignTakeoff && game1.Takeoff is { } takeoff && lane.Script.Count > 0)
            {
                int runUp = RunUpMatching(lane, profile, takeoff.Y);
                played = lane with { Script = [lane.Script[0] with { Ticks = runUp }, .. lane.Script.Skip(1)] };
                aligned = Inv($"; run-up {runUp} ticks to leave the ground at y {takeoff.Y:0.0} as the game did");
            }

            CalibrationOutcome sim = Simulate(played, profile);

            Vec3 d = game1.FinalEye - sim.FinalEye;
            bool final = MathF.Abs(d.X) <= FinalTolerance && MathF.Abs(d.Y) <= FinalTolerance && MathF.Abs(d.Z) <= FinalTolerance;
            bool peaks = !float.IsNaN(game1.ApexEyeZ) && !float.IsNaN(sim.ApexEyeZ);
            float dApex = peaks ? game1.ApexEyeZ - sim.ApexEyeZ : 0;
            bool apex = !peaks || MathF.Abs(dApex) <= ApexTolerance;
            string note = Inv($"final differs by ({d.X:0.0}, {d.Y:0.0}, {d.Z:0.0})") + (peaks ? Inv($", peak by {dApex:0.0}") : string.Empty) + aligned;
            bool agrees = final && apex;
            bool known = !agrees && lane.KnownDifference is not null;
            rows.Add(new CalibrationRow(lane.Name, game1, sim, agrees, known ? note + "; known: " + lane.KnownDifference : note, known));
        }

        return rows;
    }

    /// <summary>
    /// The run-up length, in ticks, after which the simulator's player
    /// (settled at the lane's start, then holding the script's first
    /// stretch) is nearest <paramref name="y"/>: where the jump that follows
    /// leaves the ground. The search stops at a second's worth past the
    /// script's own length.
    /// </summary>
    internal static int RunUpMatching(CalibrationLane lane, MovementProfile profile, float y)
    {
        PlayerMoveSim sim = new(new HullTraceWorld([.. lane.Solids.Select(s => s.ToBrush())]), profile);
        PlayerState state = sim.Spawn(lane.Start);
        for (int t = 0; t < SettleTicks; t++)
        {
            state = sim.Tick(state, new MoveInput(90, 0, 0, false, false));
        }

        CalibrationPhase run = lane.Script[0];
        int best = 1;
        float bestGap = float.MaxValue;
        for (int t = 1; t <= run.Ticks + 66; t++)
        {
            state = sim.Tick(state, new MoveInput(90, run.Forward ? ForwardMove : 0, 0, run.Jump, run.Duck));
            float gap = MathF.Abs(state.Origin.Y - y);
            if (gap < bestGap)
            {
                (best, bestGap) = (t, gap);
            }
        }

        return best;
    }

    /// <summary>
    /// The upward launch speed that rises a player's eye by
    /// <paramref name="eyeRise"/> at the peak. Standing, the feet rose as far
    /// as the eye. Crouched in the air, the feet were lifted by the
    /// difference of the hull heights without any speed and the eye dropped
    /// to the crouched height, so the launch alone rose the feet by
    /// <c>eyeRise - (duck eye - stand eye) - (stand height - duck height)</c>.
    /// </summary>
    /// <param name="eyeRise">How far the eye rose from standing at the start.</param>
    /// <param name="crouched">Whether the player was crouched at the peak.</param>
    /// <param name="profile">The movement numbers.</param>
    /// <returns>Units per second.</returns>
    public static float LaunchSpeed(float eyeRise, bool crouched, MovementProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        float feet = crouched
            ? eyeRise - (profile.DuckEyeHeight - profile.EyeHeight) - (profile.StandHeight - profile.DuckHeight)
            : eyeRise;
        return MathF.Sqrt(2f * profile.Gravity * Math.Max(0f, feet));
    }

    /// <summary>Writes the comparison as a fixed-width table, one lane per line, and a summary.</summary>
    /// <param name="rows">The rows.</param>
    /// <returns>The text.</returns>
    public static string Format(IReadOnlyList<CalibrationRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        StringBuilder text = new();
        text.Append("lane            agree  game eye (x y z)          sim eye (x y z)           note\n");
        foreach (CalibrationRow r in rows)
        {
            text.Append(Inv($"{r.Lane,-15} {(r.Agrees ? "yes" : r.Known ? "known" : "NO"),-6} {Eye(r.Game),-25} {Eye(r.Sim),-25} {r.Note}\n"));
        }

        int known = rows.Count(r => r.Known);
        text.Append(Inv($"{rows.Count(r => r.Agrees)} of {rows.Count} lanes agree"))
            .Append(known > 0 ? Inv($", {known} differ as known") : string.Empty).Append('\n');
        return text.ToString();

        static string Eye(CalibrationOutcome? o) => o is { } v ? Inv($"{v.FinalEye.X:0.0} {v.FinalEye.Y:0.0} {v.FinalEye.Z:0.0}") : "-";
    }
}
