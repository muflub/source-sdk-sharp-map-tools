//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// The navigation of a real stock map, <c>dm_lockdown</c> (BSP 19, version 0
/// leaves, displacements, ladders, static props), built from the repo's copy
/// without the game's content, so props are left out: a 32-wide player,
/// standing (72) or crouched (36), fitted at the voxel's centre, walking,
/// jumping and climbing, stands at all nine deathmatch spawns and reaches
/// all 77 weapons and items in one component. Fitted anywhere in its 16-unit
/// voxel instead, it does not: the file's own fit needs 48 units across.
/// </summary>
public sealed class LevelNavFromBspStockMapTests
{
    private const string Map = "game/mod_hl2mp/maps/dm_lockdown.bsp";

    [RepoSourceFact(Map)]
    public async Task EverySpawnWeaponAndItemOfDmLockdownIsInOnePlayerComponent()
    {
        byte[] bytes = await File.ReadAllBytesAsync(RepoSourceFactAttribute.Find(Map)!);
        BspData bsp = await BspFile.LoadAsync(new MemoryStream(bytes, writable: false));
        BspNavResult result = LevelNavFromBsp.Build(
            bsp, Nav3dBspBinding.Of(bytes), null, new BspNavOptions { MaxDegree = Environment.ProcessorCount });
        Nav3dReader nav = Nav3dReader.Open(Nav3dWriter.Write(result.Level));
        Assert.True(nav.MatchesBsp(bytes));
        Assert.Equal(0, bsp[BspLump.Leafs].Version);

        IReadOnlyList<NavTargets.Target> targets = NavTargets.Of(bsp);
        Assert.Equal((9, 77), (targets.Count(t => t.IsSpawn), targets.Count(t => !t.IsSpawn)));

        NavTargets.Result centre = NavTargets.Check(nav, targets, Nav3dWalker.Player);
        Assert.True(centre.AllJoined, centre.ToString());

        NavTargets.Result anywhere = NavTargets.Check(nav, targets, Nav3dWalker.Player with { Fit = Nav3dFit.Anywhere });
        Assert.False(anywhere.AllJoined, anywhere.ToString());
        Assert.True(anywhere.InMain < targets.Count, anywhere.ToString());
    }
}
