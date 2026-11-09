//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;
using SourceSharp.MapFormats.Numerics;

namespace SourceSharp.MapTools.Movement;

/// <summary>
/// A kind of launch a player gets in the air by: its speed range, how steep,
/// how far it can veer from the way the player faces, and whether it starts
/// from a running jump.
/// </summary>
/// <param name="Name">The launch's name (<c>rocket_jump</c>, ...).</param>
/// <param name="MinSpeed">The slowest launch, units per second.</param>
/// <param name="MaxSpeed">The fastest.</param>
/// <param name="MinElevation">The shallowest angle above horizontal, degrees.</param>
/// <param name="MaxElevation">The steepest.</param>
/// <param name="AzimuthSpread">How far either side of the facing the push can point, degrees.</param>
/// <param name="RunningJump">Whether the player runs and jumps before the push (a blast jump), or is pushed from a run (airblast, charge).</param>
public sealed record LaunchKind(string Name, float MinSpeed, float MaxSpeed, float MinElevation, float MaxElevation, float AzimuthSpread, bool RunningJump)
{
    /// <summary>
    /// A soldier's rocket jump. Blast knockback is damage × (48·48·82 ÷ hull
    /// volume) × scale, capped at 1000; airborne self-damage scales by 10,
    /// and a crouched hull counts as 55 high, so a crouched rocket jump
    /// pushes 800 to 1000 units a second, mostly upward.
    /// </summary>
    public static LaunchKind RocketJump { get; } = new("rocket_jump", 550f, 1000f, 40f, 85f, 35f, true);

    /// <summary>A demoman's sticky jump: one to three stickies, each push capped at 1000, so up to about 2000.</summary>
    public static LaunchKind StickyJump { get; } = new("sticky_jump", 800f, 2000f, 50f, 88f, 30f, true);

    /// <summary>A grenade or pipe jump: a single blast from the ground, weaker than a rocket.</summary>
    public static LaunchKind GrenadeJump { get; } = new("grenade_jump", 400f, 800f, 35f, 80f, 40f, true);

    /// <summary>A pyro's airblast on a player: 600 units a second along the blast, lifted off the ground.</summary>
    public static LaunchKind Airblast { get; } = new("airblast", 450f, 700f, 15f, 45f, 180f, false);

    /// <summary>A demoman's shield charge: 750 units a second along the ground.</summary>
    public static LaunchKind Charge { get; } = new("charge", 700f, 750f, 0f, 5f, 0f, false);

    /// <summary>Every TF2 launch.</summary>
    public static IReadOnlyList<LaunchKind> Tf2 { get; } = [RocketJump, StickyJump, GrenadeJump, Airblast, Charge];
}

/// <summary>What happened to a player in the air.</summary>
public enum AirborneEventKind
{
    /// <summary>The player's box ended up inside a solid.</summary>
    Stuck,

    /// <summary>The player stopped moving while airborne: wedged in a gap or a V between steep surfaces.</summary>
    Wedged,

    /// <summary>The player caught a ledge's lip it nearly cleared and lost most of its speed.</summary>
    LipCatch,

    /// <summary>The player caught a thin solid's edge (a rail, a trim, a prop's edge) and lost most of its speed.</summary>
    EdgeCatch,

    /// <summary>The player hit a ceiling while flying up.</summary>
    CeilingBonk,

    /// <summary>The player reached space outside the playable world.</summary>
    OutOfBounds,
}

/// <summary>One event in one flight.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Position">Where the feet were.</param>
/// <param name="Brush">The solid involved, or -1.</param>
/// <param name="Launch">The launch that led to it.</param>
public readonly record struct AirborneEvent(AirborneEventKind Kind, Vec3 Position, int Brush, string Launch);

/// <summary>Events of one kind gathered at one spot.</summary>
/// <param name="Kind">What happens there.</param>
/// <param name="Count">How many flights it happened in.</param>
/// <param name="Centre">The mean position.</param>
/// <param name="Min">The spot's lower bound.</param>
/// <param name="Max">The spot's upper bound.</param>
/// <param name="Brush">The solid most often involved, or -1.</param>
/// <param name="Launches">The launches that led there, by name.</param>
public sealed record EventCluster(AirborneEventKind Kind, int Count, Vec3 Centre, Vec3 Min, Vec3 Max, int Brush, IReadOnlyList<string> Launches);

/// <summary>How the airborne fuzz runs.</summary>
public sealed record AirborneOptions
{
    /// <summary>Gets the default: 2000 flights of every TF2 launch, seed 1.</summary>
    public static AirborneOptions Default { get; } = new();

    /// <summary>Gets the number of flights.</summary>
    public int Trajectories { get; init; } = 2000;

    /// <summary>Gets the seed: the same seed gives the same flights and the same result.</summary>
    public ulong Seed { get; init; } = 1;

    /// <summary>Gets the launches to sample from, uniformly.</summary>
    public IReadOnlyList<LaunchKind> Launches { get; init; } = LaunchKind.Tf2;

    /// <summary>Gets the most ticks one flight runs (five seconds).</summary>
    public int MaxTicks { get; init; } = 330;

    /// <summary>Gets the threads flights run on; -1 for the runtime's default.</summary>
    public int MaxDegreeOfParallelism { get; init; } = -1;

    /// <summary>Gets the spot size events are gathered in, units.</summary>
    public float ClusterSize { get; init; } = 64f;
}

/// <summary>What the airborne fuzz found.</summary>
/// <param name="Trajectories">The flights run.</param>
/// <param name="Clusters">The spots where events gathered, most frequent first.</param>
/// <param name="ExplosiveOnly">Floor nodes no spawn reaches on foot but a flight landed on.</param>
public sealed record AirborneResult(int Trajectories, IReadOnlyList<EventCluster> Clusters, IReadOnlyList<int> ExplosiveOnly);

/// <summary>
/// Throws simulated players through the air from the floor a map's spawns
/// reach, the ways TF2 players fly (rocket, sticky and grenade jumps,
/// airblast, charges), and records where flights go wrong: a player stuck in
/// a solid, wedged in a gap, caught on a lip it nearly cleared or on a thin
/// edge, bonking a ceiling, or leaving the playable world.
/// </summary>
/// <remarks>
/// <para>
/// Each flight picks, from a seeded generator, a reached floor node, a
/// facing, a launch kind and a speed and angle within the kind's range; a
/// blast jump first runs and jumps, then takes the push on the next tick.
/// The player then holds forward along the push (air strafing toward where
/// it is flying, as players do) and crouches at the top of the climb, until
/// it lands and settles or the tick budget runs out.
/// </para>
/// <para>
/// A catch is a contact in the air that removes more than half of a fast
/// horizontal speed (over 300) against a near-vertical surface. It is a lip
/// catch when the solid's top is within a step and a half above the feet,
/// the player all but cleared it, and an edge catch when the solid is under
/// 24 units thick across, a rail or a trim. Those are the snags players
/// complain about; a full-on wall hit is neither.
/// </para>
/// <para>
/// Events are gathered into spots on a grid of
/// <see cref="AirborneOptions.ClusterSize"/>, joined where neighbouring cells
/// share a kind, and ranked by how many flights met them. Flights are
/// independent and seeded per index, so the result is the same at any
/// thread count.
/// </para>
/// </remarks>
public static class AirborneFuzzer
{
    /// <summary>Flies the trajectories.</summary>
    /// <param name="world">The collision.</param>
    /// <param name="profile">The movement numbers of the player flown.</param>
    /// <param name="reach">The reachability result whose reached floor the flights start from.</param>
    /// <param name="options">The fuzz options.</param>
    /// <param name="cancellationToken">Cancels the fuzz.</param>
    /// <returns>The result.</returns>
    public static AirborneResult Analyze(
        HullTraceWorld world,
        MovementProfile profile,
        ReachabilityResult reach,
        AirborneOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(reach);
        AirborneOptions o = options ?? AirborneOptions.Default;
        bool[] reached = new bool[reach.Nodes.Count];
        foreach (bool[] r in reach.ReachedBy.Values)
        {
            for (int i = 0; i < r.Length; i++)
            {
                reached[i] |= r[i];
            }
        }

        int[] starts = [.. Enumerable.Range(0, reach.Nodes.Count).Where(i => reached[i])];
        if (starts.Length == 0 || o.Trajectories <= 0 || o.Launches.Count == 0)
        {
            return new AirborneResult(0, [], []);
        }

        PlayerMoveSim sim = new(world, profile);
        NodeIndex nodes = new(reach.Nodes, 32f);
        List<AirborneEvent>[] events = new List<AirborneEvent>[o.Trajectories];
        int[] landings = new int[o.Trajectories];
        ParallelOptions parallel = new() { MaxDegreeOfParallelism = o.MaxDegreeOfParallelism, CancellationToken = cancellationToken };
        System.Threading.Tasks.Parallel.For(0, o.Trajectories, parallel, t =>
        {
            Random64 random = new(o.Seed, (ulong)t);
            Vec3 start = reach.Nodes[starts[random.Below(starts.Length)]];
            LaunchKind launch = o.Launches[random.Below(o.Launches.Count)];
            float yaw = random.Between(0f, 360f);
            float speed = random.Between(launch.MinSpeed, launch.MaxSpeed);
            float elevation = random.Between(launch.MinElevation, launch.MaxElevation);
            float azimuth = yaw + random.Between(-launch.AzimuthSpread, launch.AzimuthSpread);
            (events[t], Vec3? landed) = Fly(sim, start, yaw, launch, speed, elevation, azimuth, o.MaxTicks);
            landings[t] = landed is { } p ? nodes.Nearest(p, profile.StandHeight * 0.5f) : -1;
        });

        SortedSet<int> explosiveOnly = [.. landings.Where(n => n >= 0 && !reached[n])];
        return new AirborneResult(o.Trajectories, Cluster(events, o.ClusterSize), [.. explosiveOnly]);
    }

    /// <summary>Flies one trajectory and records its events.</summary>
    /// <returns>The events, and where it landed when it came to rest on the ground.</returns>
    internal static (List<AirborneEvent> Events, Vec3? Landed) Fly(
        PlayerMoveSim sim, Vec3 start, float yaw, LaunchKind launch, float speed, float elevation, float azimuth, int maxTicks)
    {
        MovementProfile profile = sim.Profile;
        List<AirborneEvent> found = [];
        HashSet<AirborneEventKind> seen = [];
        void Record(AirborneEventKind kind, Vec3 at, int brush)
        {
            if (seen.Add(kind))
            {
                found.Add(new AirborneEvent(kind, at, brush, launch.Name));
            }
        }

        PlayerState state = sim.Spawn(start);
        if (!state.OnGround)
        {
            return (found, null);
        }

        float rad = elevation * (MathF.PI / 180f);
        float az = azimuth * (MathF.PI / 180f);
        float horizontal = speed * DetMathF.Cos(rad);
        Vec3 push = new(horizontal * DetMathF.Cos(az), horizontal * DetMathF.Sin(az), speed * DetMathF.Sin(rad));

        // Blast jumps run a few ticks and jump, and take the blast the tick
        // after: the explosion lands as the player leaves the ground.
        int runUp = launch.RunningJump ? 12 : 6;
        for (int t = 0; t < runUp; t++)
        {
            state = sim.Tick(state, new MoveInput(yaw, profile.MaxSpeed, 0, launch.RunningJump && t == runUp - 1, false));
        }

        state = state with { Velocity = state.Velocity + push, OnGround = false };
        float flyYaw = azimuth;
        int still = 0, grounded = 0;
        List<MoveContact> contacts = [];
        for (int t = 0; t < maxTicks; t++)
        {
            contacts.Clear();
            Vec3 before = state.Origin;
            Vec3 velocityBefore = state.Velocity;
            bool duck = !state.OnGround && state.Velocity.Z < 150f;
            state = sim.Tick(state, new MoveInput(flyYaw, profile.MaxSpeed, 0, false, duck), contacts);

            if (sim.World.IsStuck(state.Origin, profile.Mins(state.Ducked), profile.Maxs(state.Ducked)))
            {
                Record(AirborneEventKind.Stuck, state.Origin, -1);
                break;
            }

            if (!sim.World.IsOpen(state.Origin + new Vec3(0, 0, 8)))
            {
                Record(AirborneEventKind.OutOfBounds, state.Origin, -1);
                break;
            }

            float hBefore = MathF.Sqrt((velocityBefore.X * velocityBefore.X) + (velocityBefore.Y * velocityBefore.Y));
            foreach (MoveContact c in contacts)
            {
                if (c.Normal.Z < -0.7f && velocityBefore.Z > 200f)
                {
                    Record(AirborneEventKind.CeilingBonk, c.Position, c.Brush);
                }

                bool wall = MathF.Abs(c.Normal.Z) < 0.3f;
                if (!state.OnGround && wall && hBefore > 300f && c.SpeedLost > hBefore * 0.5f && c.Brush >= 0)
                {
                    NavBrush solid = sim.World.Solids[c.Brush];
                    float lip = (float)solid.MaxZ - c.Position.Z;
                    if (lip > 0 && lip <= profile.StepHeight * 1.5f)
                    {
                        Record(AirborneEventKind.LipCatch, c.Position, c.Brush);
                    }
                    else if (IsSmall(solid))
                    {
                        Record(AirborneEventKind.EdgeCatch, c.Position, c.Brush);
                    }
                }
            }

            float moved = (state.Origin - before).Length();
            still = !state.OnGround && moved < 0.5f ? still + 1 : 0;
            if (still >= 10)
            {
                Record(AirborneEventKind.Wedged, state.Origin, contacts.Count > 0 ? contacts[0].Brush : -1);
                break;
            }

            grounded = state.OnGround ? grounded + 1 : 0;
            if (grounded >= 5)
            {
                return (found, state.Origin);
            }

            // Strafe toward where the flight is heading, as players hold it.
            if (state.Velocity.X * state.Velocity.X + state.Velocity.Y * state.Velocity.Y > 1f)
            {
                flyYaw = DetMathF.Atan2(state.Velocity.Y, state.Velocity.X) * (180f / MathF.PI);
            }
        }

        return (found, null);
    }

    /// <summary>
    /// Whether a solid is a small thing a flight can catch on: under
    /// <see cref="SmallThickness"/> across one way and under
    /// <see cref="SmallLength"/> the other (a post, a rail, a beam end, a
    /// prop's piece), as opposed to a wall, which is thin but long and which a
    /// flight is meant to hit.
    /// </summary>
    /// <param name="solid">The solid.</param>
    /// <returns>True when small.</returns>
    public static bool IsSmall(NavBrush solid)
    {
        ArgumentNullException.ThrowIfNull(solid);
        double a = solid.MaxX - solid.MinX, b = solid.MaxY - solid.MinY;
        return Math.Min(a, b) < SmallThickness && Math.Max(a, b) < SmallLength;
    }

    /// <summary>The most a small solid measures across its narrow way, units.</summary>
    public const float SmallThickness = 24f;

    /// <summary>The most a small solid measures across its long way, units.</summary>
    public const float SmallLength = 64f;

    /// <summary>
    /// Whether an event kind is a problem to fix (stuck, wedged, a catch, out
    /// of bounds) rather than information (a ceiling bonk, which every low
    /// roof over a jumping spot produces).
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns>True for problems.</returns>
    public static bool IsProblem(AirborneEventKind kind) => kind != AirborneEventKind.CeilingBonk;

    private static List<EventCluster> Cluster(List<AirborneEvent>[] perFlight, float size)
    {
        Dictionary<(AirborneEventKind, int, int, int), List<AirborneEvent>> cells = [];
        foreach (List<AirborneEvent> flight in perFlight)
        {
            foreach (AirborneEvent e in flight)
            {
                (AirborneEventKind, int, int, int) key = (e.Kind, (int)MathF.Floor(e.Position.X / size), (int)MathF.Floor(e.Position.Y / size), (int)MathF.Floor(e.Position.Z / size));
                if (!cells.TryGetValue(key, out List<AirborneEvent>? list))
                {
                    cells[key] = list = [];
                }

                list.Add(e);
            }
        }

        // Join touching cells of the same kind into one spot.
        HashSet<(AirborneEventKind, int, int, int)> done = [];
        List<EventCluster> clusters = [];
        foreach ((AirborneEventKind, int, int, int) key in cells.Keys.OrderBy(k => k))
        {
            if (!done.Add(key))
            {
                continue;
            }

            List<AirborneEvent> members = [];
            Stack<(AirborneEventKind, int, int, int)> stack = new([key]);
            while (stack.Count > 0)
            {
                (AirborneEventKind kind, int x, int y, int z) = stack.Pop();
                members.AddRange(cells[(kind, x, y, z)]);
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            (AirborneEventKind, int, int, int) next = (kind, x + dx, y + dy, z + dz);
                            if (cells.ContainsKey(next) && done.Add(next))
                            {
                                stack.Push(next);
                            }
                        }
                    }
                }
            }

            Vec3 sum = Vec3.Zero;
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            foreach (AirborneEvent e in members)
            {
                sum += e.Position;
                minX = MathF.Min(minX, e.Position.X);
                minY = MathF.Min(minY, e.Position.Y);
                minZ = MathF.Min(minZ, e.Position.Z);
                maxX = MathF.Max(maxX, e.Position.X);
                maxY = MathF.Max(maxY, e.Position.Y);
                maxZ = MathF.Max(maxZ, e.Position.Z);
            }

            int brush = members.Where(e => e.Brush >= 0).GroupBy(e => e.Brush).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstOrDefault(-1);
            clusters.Add(new EventCluster(
                key.Item1,
                members.Count,
                sum * (1f / members.Count),
                new Vec3(minX, minY, minZ),
                new Vec3(maxX, maxY, maxZ),
                brush,
                [.. members.Select(e => e.Launch).Distinct().Order(StringComparer.Ordinal)]));
        }

        return [.. clusters.OrderByDescending(c => c.Count).ThenBy(c => c.Kind).ThenBy(c => c.Centre.X).ThenBy(c => c.Centre.Y).ThenBy(c => c.Centre.Z)];
    }

    /// <summary>Finds the floor node nearest a point.</summary>
    private sealed class NodeIndex
    {
        private readonly IReadOnlyList<Vec3> _nodes;
        private readonly float _cell;
        private readonly Dictionary<(int, int), List<int>> _cells = [];

        public NodeIndex(IReadOnlyList<Vec3> nodes, float cell)
        {
            _nodes = nodes;
            _cell = cell;
            for (int i = 0; i < nodes.Count; i++)
            {
                (int, int) key = Key(nodes[i]);
                if (!_cells.TryGetValue(key, out List<int>? list))
                {
                    _cells[key] = list = [];
                }

                list.Add(i);
            }
        }

        public int Nearest(Vec3 p, float tolerance)
        {
            (int cx, int cy) = Key(p);
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int y = cy - 1; y <= cy + 1; y++)
            {
                for (int x = cx - 1; x <= cx + 1; x++)
                {
                    if (!_cells.TryGetValue((x, y), out List<int>? list))
                    {
                        continue;
                    }

                    foreach (int n in list)
                    {
                        Vec3 q = _nodes[n];
                        if (MathF.Abs(q.Z - p.Z) > tolerance)
                        {
                            continue;
                        }

                        float d = (q - p).LengthSquared();
                        if (d < bestDistance || (d == bestDistance && n < best))
                        {
                            bestDistance = d;
                            best = n;
                        }
                    }
                }
            }

            return best;
        }

        private (int, int) Key(Vec3 p) => ((int)MathF.Floor(p.X / _cell), (int)MathF.Floor(p.Y / _cell));
    }

    /// <summary>
    /// A small seeded generator (SplitMix64), one per flight: the flight's
    /// index is mixed into the seed so flights can run in any order.
    /// </summary>
    private struct Random64(ulong seed, ulong stream)
    {
        private ulong _state = seed ^ (stream * 0x9E3779B97F4A7C15UL) ^ 0xD1B54A32D192ED03UL;

        public int Below(int n) => (int)(Next() % (ulong)n);

        public float Between(float lo, float hi) => lo + ((hi - lo) * ((Next() >> 40) / (float)(1UL << 24)));

        private ulong Next()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
