//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// What a deathmatch bot must reach on a map: the player spawns, and the
/// weapons and items (chargers among them), read from the entity lump; and
/// whether a navigation's walking components join them.
/// </summary>
/// <remarks>
/// <para>
/// <b>A spawn</b> is where a player appears: the leaf holding its origin
/// (or, for an origin a little under a voxel's floor, the voxel above),
/// followed down to its floor, must be a place to stand, and that is the
/// player's place.
/// </para>
/// <para>
/// <b>A weapon or item</b> is reached when a player stands where its box
/// touches it: a place to stand within two voxel columns of it (the
/// player's half-width plus half a voxel, and the item's own half-width),
/// whose floor is at most the player's height below it and not above it. A
/// charger hangs on a wall at chest height and a box of ammunition can lie
/// on a shelf, so its own voxel is often solid.
/// </para>
/// </remarks>
internal static class NavTargets
{
    /// <summary>One thing to reach.</summary>
    public sealed record Target(string ClassName, Vec3 Origin)
    {
        public bool IsSpawn => ClassName == "info_player_deathmatch";

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{ClassName} ({Origin.X:0.#} {Origin.Y:0.#} {Origin.Z:0.#})");
    }

    /// <summary>The counts a check gives.</summary>
    public sealed record Result(
        int Spawns, int Items, int SpawnsNotFitting, int Unreached, int Components, int InMain, IReadOnlyList<string> Outside, int LeafCount)
    {
        public bool AllJoined => SpawnsNotFitting == 0 && Unreached == 0 && InMain == Spawns + Items;

        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"{LeafCount} leaves, {Components} walking components; {Spawns} spawns ({SpawnsNotFitting} where the player does not fit), {Items} weapons and items ({Unreached} unreached); {InMain} of {Spawns + Items} in the largest target component; outside it: {string.Join(", ", Outside)}");
    }

    /// <summary>The spawns, weapons and items of a map's entity lump.</summary>
    public static IReadOnlyList<Target> Of(BspData bsp) =>
        [.. EntityLump.Parse(bsp[BspLump.Entities])
            .Where(e => e.ClassName == "info_player_deathmatch" || e.ClassName.StartsWith("weapon_", StringComparison.Ordinal)
                || e.ClassName.StartsWith("item_", StringComparison.Ordinal))
            .Select(e => new Target(e.ClassName, NavGeometry.TryVector(e.Get("origin"), out Vec3 v) ? v : throw new InvalidOperationException($"{e.ClassName} has no origin")))];

    /// <summary>Joins the targets to a walker's components.</summary>
    public static Result Check(Nav3dReader nav, IReadOnlyList<Target> targets, Nav3dWalker walker)
    {
        (int[] component, int count) = Nav3dWalk.Components(nav, walker);
        int notFitting = 0;
        int unreached = 0;
        List<(Target Target, HashSet<int> Components)> placed = [];
        foreach (Target target in targets)
        {
            HashSet<int> found = [];
            if (target.IsSpawn)
            {
                int leaf = Floor(nav, target.Origin, walker.ClipClass);
                if (leaf < 0 || component[leaf] < 0 || !Nav3dWalk.IsStand(nav, walker, leaf))
                {
                    notFitting++;
                    continue;
                }

                found.Add(component[leaf]);
            }
            else
            {
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        for (float dz = 1; dz >= -72; dz -= nav.VoxelSize / 2)
                        {
                            int leaf = Floor(nav, target.Origin + new Vec3(dx * nav.VoxelSize, dy * nav.VoxelSize, dz), walker.ClipClass);
                            if (leaf >= 0 && component[leaf] >= 0 && Nav3dWalk.IsStand(nav, walker, leaf))
                            {
                                float floor = nav.Leaf(leaf).FloorZ(walker.ClipClass);
                                if (floor <= target.Origin.Z + 1 && floor >= target.Origin.Z - walker.StandHeight)
                                {
                                    found.Add(component[leaf]);
                                }
                            }
                        }
                    }
                }

                if (found.Count == 0)
                {
                    unreached++;
                    continue;
                }
            }

            placed.Add((target, found));
        }

        int main = placed.SelectMany(p => p.Components).GroupBy(c => c).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstOrDefault(-1);
        List<string> outside = [.. placed.Where(p => !p.Components.Contains(main)).Select(p => p.Target.ToString())];
        return new Result(
            targets.Count(t => t.IsSpawn), targets.Count(t => !t.IsSpawn), notFitting, unreached, count, placed.Count - outside.Count, outside, nav.LeafCount);
    }

    /// <summary>The floor leaf under a point: its voxel's leaf, or the voxel above's for a point just under a floor, followed down to where it stands.</summary>
    private static int Floor(Nav3dReader nav, Vec3 point, Nav3dClipClass clipClass)
    {
        int leaf = nav.FindLeaf(point + new Vec3(0, 0, 1));
        if (leaf < 0)
        {
            leaf = nav.FindLeaf(point + new Vec3(0, 0, 1 + nav.VoxelSize));
        }

        while (leaf >= 0 && !nav.Leaf(leaf).IsGrounded(clipClass))
        {
            leaf = nav.LeafBelow(leaf);
        }

        return leaf;
    }
}
