//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Movement;

/// <summary>How reachability is sampled.</summary>
public sealed record ReachabilityOptions
{
    /// <summary>Gets the default options.</summary>
    public static ReachabilityOptions Default { get; } = new();

    /// <summary>Gets the spacing of the floor sample grid, in units.</summary>
    public float Spacing { get; init; } = 32f;

    /// <summary>Gets a value indicating whether to try jumps (crouch jumps) as well as walks.</summary>
    public bool Jumps { get; init; } = true;

    /// <summary>
    /// Gets the most ticks one walk or jump is simulated for: long enough for
    /// a fall of a couple of thousand units (about 2.3 s), so walking off a
    /// deep drop still lands and counts as a move.
    /// </summary>
    public int MaxTicks { get; init; } = 160;

    /// <summary>Gets the threads nodes are explored on; -1 for the runtime's default.</summary>
    public int MaxDegreeOfParallelism { get; init; } = -1;

    /// <summary>Gets the size below which a sink region counts as a trap rather than a play area, in nodes.</summary>
    public int TrapLimit { get; init; } = 400;
}

/// <summary>A spawn the analysis starts from.</summary>
/// <param name="Team">The team number (0 for none).</param>
/// <param name="Origin">Where it stands.</param>
public readonly record struct ReachSpawn(int Team, Vec3 Origin);

/// <summary>A group of floor nodes reported together, with where to look.</summary>
/// <param name="Nodes">The node indices.</param>
/// <param name="Min">The group's lower bound (feet positions).</param>
/// <param name="Max">The group's upper bound.</param>
/// <param name="Sample">A representative position: the node nearest the group's centre.</param>
public sealed record NodeGroup(IReadOnlyList<int> Nodes, Vec3 Min, Vec3 Max, Vec3 Sample);

/// <summary>What reachability found.</summary>
/// <param name="Nodes">Every floor position sampled (the feet).</param>
/// <param name="Edges">The moves that work, per node: the nodes a walk or jump from it ends on.</param>
/// <param name="ReachedBy">Per team, whether each node is reachable from that team's spawns.</param>
/// <param name="SpawnNodes">The node each spawn stands on, or -1 when it stands on none (a stuck or floating spawn).</param>
/// <param name="Islands">Groups of floor no spawn reaches.</param>
/// <param name="Traps">Groups reachable from a spawn that, once entered, cannot be left (short of dying).</param>
public sealed record ReachabilityResult(
    IReadOnlyList<Vec3> Nodes,
    IReadOnlyList<int[]> Edges,
    IReadOnlyDictionary<int, bool[]> ReachedBy,
    IReadOnlyList<int> SpawnNodes,
    IReadOnlyList<NodeGroup> Islands,
    IReadOnlyList<NodeGroup> Traps);

/// <summary>
/// Where a player can get to, found by simulating the moves: every floor a
/// player can stand on is sampled, and from each, walks and jumps in eight
/// directions are run through <see cref="PlayerMoveSim"/> to see where they
/// end. The result is a directed graph: a drop can be walked off and not
/// climbed back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Floors.</b> A column every <see cref="ReachabilityOptions.Spacing"/>
/// units is searched from the top of the bounds down: the player's hull is
/// dropped until it lands; a landing on standable ground (normal steep
/// enough, room for the hull, and open space in the map's tree rather than
/// void the compiler sealed off) is a node, and the search continues from
/// the first free space under that floor, so stacked floors each get a node.
/// </para>
/// <para>
/// <b>Moves.</b> From each node the simulator walks toward each of the eight
/// neighbouring columns, and, when jumps are on, jumps toward each with the
/// crouch held from the top of the jump, the way players clear ledges. A move
/// ends when the player is back on the ground (or the tick budget runs out
/// in the air); the node nearest where it stopped, within half the hull's
/// height, is the edge's end (stacked floors are at least a hull apart, and a
/// slope can rise more than a step between columns). Moves are independent, so nodes are explored in
/// parallel with the same result at any thread count.
/// </para>
/// <para>
/// <b>Findings.</b> Reachability from each team's spawns is a search along
/// the edges. Floor no spawn reaches forms islands. The largest strongly
/// connected region of the reached graph is the play area; any other region
/// with no edge out is a trap: a pit, a ledge with no way down, a gap
/// between props.
/// </para>
/// </remarks>
public static class Reachability
{
    private static ReadOnlySpan<int> Dx => [1, 1, 0, -1, -1, -1, 0, 1];

    private static ReadOnlySpan<int> Dy => [0, 1, 1, 1, 0, -1, -1, -1];

    /// <summary>Samples the floors and simulates the moves.</summary>
    /// <param name="world">The collision.</param>
    /// <param name="profile">The movement numbers.</param>
    /// <param name="spawns">Where players start.</param>
    /// <param name="min">The lower corner of the region to sample.</param>
    /// <param name="max">The upper corner.</param>
    /// <param name="options">The sampling options.</param>
    /// <param name="cancellationToken">Cancels the analysis.</param>
    /// <returns>The result.</returns>
    public static ReachabilityResult Analyze(
        HullTraceWorld world,
        MovementProfile profile,
        IReadOnlyList<ReachSpawn> spawns,
        Vec3 min,
        Vec3 max,
        ReachabilityOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(spawns);
        ReachabilityOptions o = options ?? ReachabilityOptions.Default;
        Grid grid = new(min, max, o.Spacing);

        List<Vec3> nodes = [];
        Dictionary<(int, int), List<int>> columns = [];
        for (int iy = 0; iy < grid.Rows; iy++)
        {
            for (int ix = 0; ix < grid.Columns; ix++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (Vec3 feet in Floors(world, profile, grid.At(ix, iy), min.Z, max.Z))
                {
                    if (!columns.TryGetValue((ix, iy), out List<int>? list))
                    {
                        columns[(ix, iy)] = list = [];
                    }

                    list.Add(nodes.Count);
                    nodes.Add(feet);
                }
            }
        }

        PlayerMoveSim sim = new(world, profile);
        int[][] edges = new int[nodes.Count][];
        ParallelOptions parallel = new() { MaxDegreeOfParallelism = o.MaxDegreeOfParallelism, CancellationToken = cancellationToken };
        System.Threading.Tasks.Parallel.For(0, nodes.Count, parallel, n =>
        {
            SortedSet<int> to = [];
            Vec3 from = nodes[n];
            for (int d = 0; d < 8; d++)
            {
                float yaw = d * 45f;
                foreach (bool jump in o.Jumps ? [false, true] : new[] { false })
                {
                    Vec3? landed = Move(sim, from, yaw, jump, o.Spacing, o.MaxTicks);
                    if (landed is { } end && Nearest(grid, columns, nodes, end, profile.StandHeight * 0.5f) is int target and >= 0 && target != n)
                    {
                        to.Add(target);
                    }
                }
            }

            edges[n] = [.. to];
        });

        // A spawn inside a solid stands nowhere, however close a floor node
        // is: the player would start stuck. One a little above the floor is
        // fine; players drop onto the floor when they spawn.
        int[] spawnNodes = [.. spawns.Select(s =>
        {
            PlayerState placed = sim.Spawn(s.Origin);
            return world.IsStuck(placed.Origin, profile.Mins(false), profile.Maxs(false))
                ? -1
                : Nearest(grid, columns, nodes, placed.Origin, profile.StandHeight * 0.5f) ?? -1;
        })];
        Dictionary<int, bool[]> reachedBy = [];
        foreach (IGrouping<int, (ReachSpawn Spawn, int Node)> team in spawns.Zip(spawnNodes).GroupBy(p => p.First.Team, p => (p.First, p.Second)))
        {
            reachedBy[team.Key] = Search(edges, team.Select(t => t.Node).Where(i => i >= 0));
        }

        bool[] reached = new bool[nodes.Count];
        foreach (bool[] r in reachedBy.Values)
        {
            for (int i = 0; i < r.Length; i++)
            {
                reached[i] |= r[i];
            }
        }

        List<NodeGroup> islands = Islands(nodes, edges, reached);
        List<NodeGroup> traps = Traps(nodes, edges, reached, o.TrapLimit);
        return new ReachabilityResult(nodes, edges, reachedBy, spawnNodes, islands, traps);
    }

    /// <summary>
    /// The standable floors in one column, top down: the hull dropped from
    /// above until it lands, then from the first free space under each floor.
    /// </summary>
    /// <param name="world">The collision.</param>
    /// <param name="profile">The movement numbers (the hull and the floor slope).</param>
    /// <param name="at">The column's horizontal position.</param>
    /// <param name="bottom">The lowest height searched.</param>
    /// <param name="top">The height the search starts from.</param>
    /// <returns>The feet positions, highest first.</returns>
    public static List<Vec3> Floors(HullTraceWorld world, MovementProfile profile, (float X, float Y) at, float bottom, float top)
    {
        List<Vec3> found = [];
        Vec3 mins = profile.Mins(false), maxs = profile.Maxs(false);
        float z = top;

        // Enough steps to pass down through every unit of solid in the column
        // eight at a time, plus one drop per floor: a fixed small budget stops
        // the search inside a thick cliff above a tunnel.
        int budget = (int)((top - bottom) / 8f) + 64;
        for (int guard = 0; guard < budget && z > bottom; guard++)
        {
            Vec3 start = new(at.X, at.Y, z);
            if (world.IsStuck(start, mins, maxs))
            {
                // Inside a solid: step down until the hull fits again.
                z -= 8f;
                continue;
            }

            HullTrace drop = world.Trace(start, new Vec3(at.X, at.Y, bottom), mins, maxs);
            if (!drop.Hit)
            {
                break;
            }

            if (drop.Normal.Z >= profile.FloorNormalZ && world.IsOpen(drop.End + new Vec3(0, 0, maxs.Z * 0.5f)))
            {
                found.Add(drop.End);
            }

            // Continue below this surface: past the hull's own height.
            z = drop.End.Z - maxs.Z - 1f;
        }

        return found;
    }

    /// <summary>
    /// Simulates one move from a floor position: a walk of one spacing, or a
    /// running jump (a run-up to near full speed, then a jump, crouching from
    /// the top of it) carried on for up to six.
    /// </summary>
    /// <param name="sim">The simulator.</param>
    /// <param name="from">The feet.</param>
    /// <param name="yaw">The direction, degrees.</param>
    /// <param name="jump">Whether to jump.</param>
    /// <param name="spacing">The floor grid's spacing: how far a walk goes.</param>
    /// <param name="maxTicks">The most ticks simulated.</param>
    /// <returns>Where the player ended on the ground, or null when still airborne at the end or not standing at the start.</returns>
    public static Vec3? Move(PlayerMoveSim sim, Vec3 from, float yaw, bool jump, float spacing, int maxTicks)
    {
        PlayerState state = sim.Spawn(from);
        if (!state.OnGround)
        {
            return null;
        }

        float speed = sim.Profile.MaxSpeed;
        Vec3 start = state.Origin;
        float reach = jump ? spacing * 6f : spacing;
        bool jumped = false, left = false;
        float c = SourceSharp.MapFormats.Numerics.DetMathF.Cos(yaw * (MathF.PI / 180f));
        float sn = SourceSharp.MapFormats.Numerics.DetMathF.Sin(yaw * (MathF.PI / 180f));
        for (int t = 0; t < maxTicks; t++)
        {
            float travelled = MathF.Sqrt(((state.Origin.X - start.X) * (state.Origin.X - start.X)) + ((state.Origin.Y - start.Y) * (state.Origin.Y - start.Y)));
            bool push = travelled < reach || (jump && !jumped);

            // A jump is a running jump: run until near full speed (or for
            // twenty ticks when something holds the run back), then jump.
            // A standing jump barely moves sideways, because air control
            // only steers toward a small capped speed.
            float along = (state.Velocity.X * c) + (state.Velocity.Y * sn);
            bool press = jump && !jumped && state.OnGround && (along >= speed * 0.9f || t >= 20);
            jumped |= press;
            bool duck = jumped && !state.OnGround && state.Velocity.Z < 120f;
            state = sim.Tick(state, new MoveInput(yaw, push ? speed : 0, 0, press, duck));
            left |= jumped && !state.OnGround;
            if (state.OnGround && ((jump && left) || (!jump && !push)))
            {
                // A jump ends when it lands; a walk once it has covered the
                // spacing and is on the ground (after any fall off a ledge).
                return state.Origin;
            }
        }

        return state.OnGround ? state.Origin : null;
    }

    private static int? Nearest(Grid grid, Dictionary<(int, int), List<int>> columns, List<Vec3> nodes, Vec3 p, float tolerance)
    {
        (int ix, int iy) = grid.CellOf(p);
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int y = iy - 1; y <= iy + 1; y++)
        {
            for (int x = ix - 1; x <= ix + 1; x++)
            {
                if (!columns.TryGetValue((x, y), out List<int>? list))
                {
                    continue;
                }

                foreach (int n in list)
                {
                    Vec3 q = nodes[n];
                    float dz = MathF.Abs(q.Z - p.Z);
                    if (dz > tolerance)
                    {
                        continue;
                    }

                    float d = ((q.X - p.X) * (q.X - p.X)) + ((q.Y - p.Y) * (q.Y - p.Y)) + (dz * dz);
                    if (d < bestDistance || (d == bestDistance && n < best))
                    {
                        bestDistance = d;
                        best = n;
                    }
                }
            }
        }

        return best >= 0 ? best : null;
    }

    private static bool[] Search(int[][] edges, IEnumerable<int> starts)
    {
        bool[] seen = new bool[edges.Length];
        Queue<int> queue = new();
        foreach (int s in starts)
        {
            if (!seen[s])
            {
                seen[s] = true;
                queue.Enqueue(s);
            }
        }

        while (queue.Count > 0)
        {
            foreach (int next in edges[queue.Dequeue()])
            {
                if (!seen[next])
                {
                    seen[next] = true;
                    queue.Enqueue(next);
                }
            }
        }

        return seen;
    }

    /// <summary>Unreached nodes, grouped by connection in either direction.</summary>
    internal static List<NodeGroup> Islands(IReadOnlyList<Vec3> nodes, IReadOnlyList<int[]> edges, bool[] reached)
    {
        List<int>[] undirected = new List<int>[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            undirected[i] ??= [];
            foreach (int j in edges[i])
            {
                undirected[i].Add(j);
                (undirected[j] ??= []).Add(i);
            }
        }

        bool[] done = new bool[nodes.Count];
        List<NodeGroup> groups = [];
        for (int i = 0; i < nodes.Count; i++)
        {
            if (reached[i] || done[i])
            {
                continue;
            }

            List<int> members = [];
            Stack<int> stack = new([i]);
            done[i] = true;
            while (stack.Count > 0)
            {
                int n = stack.Pop();
                members.Add(n);
                foreach (int m in undirected[n])
                {
                    if (!reached[m] && !done[m])
                    {
                        done[m] = true;
                        stack.Push(m);
                    }
                }
            }

            members.Sort();
            groups.Add(Group(nodes, members));
        }

        return groups;
    }

    /// <summary>
    /// Reached regions with no way out: strongly connected components of the
    /// reached graph with no edge leaving them, other than the largest
    /// component (the play area) and any larger than the trap limit.
    /// </summary>
    private static List<NodeGroup> Traps(List<Vec3> nodes, int[][] edges, bool[] reached, int limit)
    {
        int[] component = StronglyConnected(edges, reached, out int count);
        int[] size = new int[count];
        bool[] exits = new bool[count];
        for (int i = 0; i < edges.Length; i++)
        {
            if (component[i] < 0)
            {
                continue;
            }

            size[component[i]]++;
            foreach (int j in edges[i])
            {
                if (component[j] != component[i])
                {
                    exits[component[i]] = true;
                }
            }
        }

        // The play area is the largest region, whether or not it has a way
        // out: when a trap exists the play area has an edge into it, so it is
        // not itself a dead end.
        int largest = -1;
        for (int c = 0; c < count; c++)
        {
            if (largest < 0 || size[c] > size[largest])
            {
                largest = c;
            }
        }

        List<NodeGroup> traps = [];
        for (int c = 0; c < count; c++)
        {
            if (exits[c] || c == largest || size[c] > limit)
            {
                continue;
            }

            List<int> members = [.. Enumerable.Range(0, edges.Length).Where(i => component[i] == c)];
            traps.Add(Group(nodes, members));
        }

        return traps;
    }

    /// <summary>Tarjan's strongly connected components over the reached nodes, iteratively.</summary>
    internal static int[] StronglyConnected(int[][] edges, bool[] include, out int count)
    {
        int n = edges.Length;
        int[] index = new int[n], low = new int[n], component = new int[n];
        bool[] onStack = new bool[n];
        Array.Fill(index, -1);
        Array.Fill(component, -1);
        Stack<int> stack = new();
        int next = 0;
        count = 0;
        Stack<(int Node, int Edge)> call = new();
        for (int root = 0; root < n; root++)
        {
            if (!include[root] || index[root] >= 0)
            {
                continue;
            }

            call.Push((root, 0));
            index[root] = low[root] = next++;
            stack.Push(root);
            onStack[root] = true;
            while (call.Count > 0)
            {
                (int v, int e) = call.Pop();
                if (e < edges[v].Length)
                {
                    call.Push((v, e + 1));
                    int w = edges[v][e];
                    if (!include[w])
                    {
                        continue;
                    }

                    if (index[w] < 0)
                    {
                        index[w] = low[w] = next++;
                        stack.Push(w);
                        onStack[w] = true;
                        call.Push((w, 0));
                    }
                    else if (onStack[w])
                    {
                        low[v] = Math.Min(low[v], index[w]);
                    }

                    continue;
                }

                if (low[v] == index[v])
                {
                    int w;
                    do
                    {
                        w = stack.Pop();
                        onStack[w] = false;
                        component[w] = count;
                    }
                    while (w != v);
                    count++;
                }

                if (call.Count > 0)
                {
                    int parent = call.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[v]);
                }
            }
        }

        return component;
    }

    private static NodeGroup Group(IReadOnlyList<Vec3> nodes, List<int> members)
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (int m in members)
        {
            Vec3 p = nodes[m];
            minX = MathF.Min(minX, p.X);
            minY = MathF.Min(minY, p.Y);
            minZ = MathF.Min(minZ, p.Z);
            maxX = MathF.Max(maxX, p.X);
            maxY = MathF.Max(maxY, p.Y);
            maxZ = MathF.Max(maxZ, p.Z);
        }

        Vec3 centre = new((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        int sample = members.OrderBy(m => (nodes[m] - centre).LengthSquared()).ThenBy(m => m).First();
        return new NodeGroup(members, new Vec3(minX, minY, minZ), new Vec3(maxX, maxY, maxZ), nodes[sample]);
    }

    /// <summary>The sampling grid over the region.</summary>
    private readonly struct Grid(Vec3 min, Vec3 max, float spacing)
    {
        public int Columns { get; } = Math.Max(1, (int)MathF.Floor((max.X - min.X) / spacing) + 1);

        public int Rows { get; } = Math.Max(1, (int)MathF.Floor((max.Y - min.Y) / spacing) + 1);

        public (float X, float Y) At(int ix, int iy) => (min.X + (ix * spacing), min.Y + (iy * spacing));

        public (int X, int Y) CellOf(Vec3 p) => ((int)MathF.Round((p.X - min.X) / spacing), (int)MathF.Round((p.Y - min.Y) / spacing));
    }
}
