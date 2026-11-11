//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapTools.Bsp.Props;
using SourceSharp.MapTools.Rooms;

namespace SourceSharp.MapTools.Nav;

/// <summary>Where a player can be in a compiled map (<see cref="NavPlayableSpace.Find"/>).</summary>
/// <param name="Bounds">The union of the play space's leaves' bounds.</param>
/// <param name="LeafInPlay">By leaf index, whether the leaf is in a play-space cluster; a solid leaf never is.</param>
public sealed record NavPlaySpace(Box Bounds, IReadOnlyList<bool> LeafInPlay)
{
    /// <summary>
    /// The bounds of each leaf in the play space, in leaf order: the boxes
    /// <see cref="Bounds"/> is the union of. A point whose leaf is in the
    /// play space lies in its leaf's box, so space no box reaches holds no
    /// play space, which lets a build skip it without walking the tree
    /// (<see cref="LevelNavFromBsp"/>). The compiler stores a leaf's bounds
    /// as whole units, which can fall up to a unit inside the leaf, so a
    /// caller pads them.
    /// </summary>
    public IReadOnlyList<Box> LeafBounds { get; init; } = [];

    /// <summary>Whether <paramref name="leaf"/> is open space a player can be in.</summary>
    /// <param name="leaf">A leaf index.</param>
    /// <returns>True for a leaf of the play space.</returns>
    public bool Holds(int leaf) => leaf >= 0 && leaf < LeafInPlay.Count && LeafInPlay[leaf];
}

/// <summary>
/// Where in a compiled map a player can be: the bounds of the vis clusters
/// joined to the player spawns, leaving out the 3D skybox.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> A compiled map's world bounds take in everything sealed
/// inside it, and a shipped map's 3D skybox is a separate sealed room that
/// is often far above or below the play space: koth_viaduct's world is
/// 16,688 units tall, far more than a <c>.nav3d</c> column holds at any
/// usable voxel. A player can never stand in the skybox, so the navigation
/// has no reason to cover it.
/// </para>
/// <para>
/// <b>How.</b> Two clusters that share a portal see each other, so the
/// clusters joined through the potentially visible sets, taken
/// transitively, are exactly a portal-connected region. The play space is
/// the region holding the player spawns (and teleport destinations, which
/// may stand in a room the spawns only reach by teleport), leaving out the
/// spawns of TF's end-of-match summary stage. The skybox is
/// the region holding the <c>sky_camera</c>; it is never joined to the play
/// space by a portal, so it falls out. When the map names no spawn the play
/// space is every cluster outside the skybox, and when the skybox camera
/// stands in the play space itself it is ignored.
/// </para>
/// <para>
/// <b>When it cannot say.</b> A map compiled without vis has no clusters to
/// join, and a map with neither a spawn nor a skybox camera gives nothing
/// to start from. Then <see cref="Of"/> answers null and the caller keeps
/// the world's bounds, which is what a map without vis (a room harness
/// compile, say) has always been built over.
/// </para>
/// </remarks>
public static class NavPlayableSpace
{
    /// <summary>
    /// How far above an entity's origin its cluster is looked up: a spawn's
    /// origin is at the player's feet and may lie on the floor's plane.
    /// </summary>
    public const float SeedLift = 16f;

    /// <summary>Whether a class name is a place a player starts or arrives at.</summary>
    /// <param name="className">An entity's class name.</param>
    /// <returns>True for the player spawns of the Source games and teleport destinations.</returns>
    public static bool IsPlayerSeed(string className) => className.ToLowerInvariant() switch
    {
        "info_player_start" or "info_player_deathmatch" or "info_player_combine" or "info_player_rebel"
            or "info_player_teamspawn" or "info_player_counterterrorist" or "info_player_terrorist"
            or "info_teleport_destination" => true,
        _ => false,
    };

    /// <summary>
    /// Whether a spawn is one the end-of-match summary stands players at
    /// (TF's <c>MatchSummary</c> key, non-zero): a stage room the map keeps
    /// apart from the play space, often at the far corner of the world, and
    /// never reached in play.
    /// </summary>
    /// <param name="entity">A spawn entity.</param>
    /// <returns>True when its <c>MatchSummary</c> key is a non-zero number.</returns>
    public static bool IsMatchSummarySpawn(BspEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return int.TryParse(entity.Get("MatchSummary"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int kind)
            && kind != 0;
    }

    /// <summary>The play space's bounds, or null when the map does not say.</summary>
    /// <param name="bsp">The compiled map.</param>
    /// <returns>The union of the bounds of the leaves in the play space's clusters, or null.</returns>
    public static Box? Of(BspData bsp) => Find(bsp)?.Bounds;

    /// <summary>The play space's bounds and which of the map's leaves it holds, or null when the map does not say.</summary>
    /// <param name="bsp">The compiled map.</param>
    /// <returns>The play space, or null.</returns>
    public static NavPlaySpace? Find(BspData bsp)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        VisibilityLump? vis = VisibilityLump.Read(bsp[BspLump.Visibility]);
        if (vis is null || vis.NumClusters == 0 || bsp[BspLump.Nodes].IsEmpty)
        {
            return null;
        }

        (short[] clusters, Box[] bounds) = Leaves(bsp[BspLump.Leafs]);
        BspTreeView tree = BspTreeView.FromBsp(bsp);
        List<int> spawns = [];
        List<int> skies = [];
        foreach (BspEntity entity in EntityLump.Parse(bsp[BspLump.Entities]))
        {
            bool spawn = IsPlayerSeed(entity.ClassName) && !IsMatchSummarySpawn(entity);
            bool camera = entity.ClassName.Equals("sky_camera", StringComparison.OrdinalIgnoreCase);
            if ((!spawn && !camera) || !NavGeometry.TryVector(entity.Get("origin"), out Vec3 origin))
            {
                continue;
            }

            int leaf = tree.LeafOf(origin + new Vec3(0, 0, SeedLift));
            int cluster = leaf >= 0 && leaf < clusters.Length ? clusters[leaf] : -1;
            if (cluster >= 0 && cluster < vis.NumClusters)
            {
                (spawn ? spawns : skies).Add(cluster);
            }
        }

        ReadOnlySpan<byte> lump = bsp[BspLump.Visibility].Data.Span;
        bool[] sky = Joined(vis, lump, skies);
        bool[] play;
        if (spawns.Count > 0)
        {
            play = Joined(vis, lump, spawns);
            if (spawns.Any(c => sky[c]))
            {
                // The camera stands in the play space: there is no separate
                // skybox to leave out.
                Array.Clear(sky);
            }
        }
        else if (skies.Count > 0)
        {
            play = new bool[vis.NumClusters];
            Array.Fill(play, true);
        }
        else
        {
            return null;
        }

        Box? union = null;
        bool[] inPlay = new bool[clusters.Length];
        List<Box> playBounds = [];
        for (int i = 0; i < clusters.Length; i++)
        {
            int c = clusters[i];
            if (c < 0 || c >= play.Length || !play[c] || sky[c])
            {
                continue;
            }

            inPlay[i] = true;
            playBounds.Add(bounds[i]);
            union = union is { } u ? new Box(Lower(u.Mins, bounds[i].Mins), Upper(u.Maxs, bounds[i].Maxs)) : bounds[i];
        }

        return union is { } found ? new NavPlaySpace(found, inPlay) { LeafBounds = playBounds } : null;
    }

    private static Vec3 Lower(Vec3 a, Vec3 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));

    private static Vec3 Upper(Vec3 a, Vec3 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    /// <summary>The clusters joined to <paramref name="seeds"/> through the potentially visible sets, the seeds included.</summary>
    private static bool[] Joined(VisibilityLump vis, ReadOnlySpan<byte> lump, IReadOnlyList<int> seeds)
    {
        bool[] seen = new bool[vis.NumClusters];
        Queue<int> queue = new();
        foreach (int s in seeds)
        {
            if (!seen[s])
            {
                seen[s] = true;
                queue.Enqueue(s);
            }
        }

        byte[] row = new byte[vis.RowBytes()];
        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            int offset = vis.BitOffset(c, VisibilityLump.Pvs);
            if (offset < 0 || offset >= lump.Length)
            {
                continue;
            }

            vis.DecompressRow(lump[offset..], row);
            for (int other = 0; other < seen.Length; other++)
            {
                if (!seen[other] && (row[other >> 3] & (1 << (other & 7))) != 0)
                {
                    seen[other] = true;
                    queue.Enqueue(other);
                }
            }
        }

        return seen;
    }

    /// <summary>Each leaf's cluster and bounds, from either leaf layout.</summary>
    private static (short[] Clusters, Box[] Bounds) Leaves(BspLumpData lump)
    {
        static Box Bounds(ShortArray3 mins, ShortArray3 maxs) => new(new Vec3(mins[0], mins[1], mins[2]), new Vec3(maxs[0], maxs[1], maxs[2]));

        if (lump.Version == 0)
        {
            ReadOnlySpan<DLeafVersion0> old = BspStructView.As<DLeafVersion0>(lump);
            short[] clusters = new short[old.Length];
            Box[] bounds = new Box[old.Length];
            for (int i = 0; i < old.Length; i++)
            {
                clusters[i] = old[i].Cluster;
                bounds[i] = Bounds(old[i].Mins, old[i].Maxs);
            }

            return (clusters, bounds);
        }

        ReadOnlySpan<DLeaf> leafs = BspStructView.As<DLeaf>(lump);
        short[] c1 = new short[leafs.Length];
        Box[] b1 = new Box[leafs.Length];
        for (int i = 0; i < leafs.Length; i++)
        {
            c1[i] = leafs[i].Cluster;
            b1[i] = Bounds(leafs[i].Mins, leafs[i].Maxs);
        }

        return (c1, b1);
    }
}
