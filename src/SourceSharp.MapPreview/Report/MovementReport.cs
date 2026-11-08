//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapPreview.Report;

/// <summary>What the movement analysis found, for <c>index.json</c>.</summary>
/// <param name="Profile">The movement profile simulated (<c>tf2_soldier</c>, ...).</param>
/// <param name="Nodes">Floor positions sampled.</param>
/// <param name="ReachedByTeam">Per team number, how many positions its spawns reach.</param>
/// <param name="StuckSpawns">Spawns that stand on no floor position (inside a solid, or with no floor under them), each with a marker.</param>
/// <param name="Traps">Regions a player can enter but not leave, largest first, each with a marker.</param>
/// <param name="Islands">Floor no spawn reaches, largest first (the ten largest).</param>
/// <param name="IslandCount">How many islands there are in all.</param>
public sealed record MovementReport(
    string Profile,
    int Nodes,
    IReadOnlyDictionary<int, int> ReachedByTeam,
    IReadOnlyList<RegionReport> StuckSpawns,
    IReadOnlyList<RegionReport> Traps,
    IReadOnlyList<RegionReport> Islands,
    int IslandCount);

/// <summary>A reported region of floor (or one spawn).</summary>
/// <param name="Nodes">How many floor positions it covers.</param>
/// <param name="Sample">A representative position, [x, y, z].</param>
/// <param name="Min">Its lower bound.</param>
/// <param name="Max">Its upper bound.</param>
/// <param name="Marker">The marker number drawn for it, or 0 when it has none.</param>
public sealed record RegionReport(int Nodes, float[] Sample, float[] Min, float[] Max, int Marker);
