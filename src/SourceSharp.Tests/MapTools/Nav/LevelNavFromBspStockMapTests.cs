//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Bsp.Structs;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// The navigation of a real stock map, <c>dm_lockdown</c> (BSP 19, version 0
/// leaves, displacements, static props), built without the game's content
/// so props are left out: every spawn (nine deathmatch, one start) has a
/// walkable floor a standing player fits on within a voxel of it, at most
/// a step below its origin, and all of them are in one component of a
/// 16-wide player-class agent.
/// </summary>
/// <remarks>
/// Within a voxel, not in it: a player fits in a voxel when it fits with
/// its origin anywhere in the voxel (format section 1), so a spawn 23 units
/// from a wall can sit in a voxel whose far edge is 8 units from it, where
/// a 32-wide player does not fit, while the voxel beside it does. Two of
/// dm_lockdown's spawns are placed so. For the same reason a 32-wide box
/// needs 48 units across to pass from voxel to voxel, and dm_lockdown's
/// ladders stand closer to their walls than that: the catwalk one spawn is
/// on is its own component for the 32-wide standing preset, joined to the
/// rest only for an agent of 16 (built here as the preset <c>narrow</c>).
/// </remarks>
public sealed class LevelNavFromBspStockMapTests
{
    private const string Map = "game/mod_hl2mp/maps/dm_lockdown.bsp";

    [RepoSourceFact(Map)]
    public async Task EverySpawnOfDmLockdownStandsInOneComponent()
    {
        byte[] bytes = await File.ReadAllBytesAsync(RepoSourceFactAttribute.Find(Map)!);
        BspData bsp = await BspFile.LoadAsync(new MemoryStream(bytes, writable: false));
        BspNavResult result = LevelNavFromBsp.Build(
            bsp, Nav3dBspBinding.Of(bytes), null, new BspNavOptions
            {
                MaxDegree = Environment.ProcessorCount,
                Settings = NavSettings.Default with { Agents = NavSettings.ParseAgents(NavSettings.DefaultAgents + "; narrow 16 72 player") },
            });
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(result.Level));
        Assert.True(nav.MatchesBsp(bytes));
        Assert.Equal(0, bsp[BspLump.Leafs].Version);

        int narrow = nav.FindPreset("narrow");
        List<Vec3> spawns = [.. EntityLump.Parse(bsp[BspLump.Entities])
            .Where(e => e.ClassName is "info_player_deathmatch" or "info_player_start")
            .Select(e => NavGeometry.TryVector(e.Get("origin"), out Vec3 v) ? v : throw new InvalidOperationException("a spawn without an origin"))];
        Assert.Equal(10, spawns.Count);

        HashSet<int> components = [];
        List<string> problems = [];
        foreach (Vec3 spawn in spawns)
        {
            int found = -1;
            for (int dy = -1; dy <= 1 && found < 0; dy++)
            {
                for (int dx = -1; dx <= 1 && found < 0; dx++)
                {
                    int leaf = nav.FindLeaf(spawn + new Vec3(dx * nav.VoxelSize, dy * nav.VoxelSize, 1));
                    while (leaf >= 0 && !nav.Leaf(leaf).IsGrounded(Nav3dClipClass.Player))
                    {
                        leaf = nav.LeafBelow(leaf);
                    }

                    if (leaf >= 0 && nav.Leaf(leaf).IsWalkable(Nav3dClipClass.Player)
                        && spawn.Z - nav.Leaf(leaf).FloorZ(Nav3dClipClass.Player) <= nav.StepHeight
                        && nav.Standable(leaf, nav.Leaf(leaf).ZLo, 32, 72, Nav3dClipClass.Player))
                    {
                        found = leaf;
                    }
                }
            }

            if (found < 0)
            {
                problems.Add($"({spawn.X} {spawn.Y} {spawn.Z})");
                continue;
            }

            components.Add(nav.Component(narrow, found));
        }

        Assert.True(problems.Count == 0, "no standable floor near " + string.Join(", ", problems));
        Assert.Single(components);
        Assert.True(nav.JumpCount > 0);
        Assert.True(nav.ObstacleCount > 0);
    }
}
