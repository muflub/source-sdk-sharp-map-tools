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
/// <param name="Airborne">The airborne fuzz, when it ran.</param>
/// <param name="CollisionWarnings">
/// What the collision could not take as the engine would: a static prop
/// model the content lacks, or one placed solid with no collision model,
/// which the engine makes non-solid.
/// </param>
public sealed record MovementReport(
    string Profile,
    int Nodes,
    IReadOnlyDictionary<int, int> ReachedByTeam,
    IReadOnlyList<RegionReport> StuckSpawns,
    IReadOnlyList<RegionReport> Traps,
    IReadOnlyList<RegionReport> Islands,
    int IslandCount,
    AirborneReport? Airborne = null,
    IReadOnlyList<string>? CollisionWarnings = null);

/// <summary>What the airborne fuzz found.</summary>
/// <param name="Trajectories">Flights run.</param>
/// <param name="ExplosiveOnlyNodes">Floor positions only blast jumps reach, not walks or jumps.</param>
/// <param name="Problems">Spots where flights went wrong (stuck, wedged, caught, out of bounds), most frequent first.</param>
/// <param name="CeilingBonks">The most frequent ceiling bonks: information, not problems.</param>
/// <param name="BlastOnlyAreas">The largest areas only blast jumps reach (roofs, ledges, map edges), largest first.</param>
public sealed record AirborneReport(
    int Trajectories, int ExplosiveOnlyNodes, IReadOnlyList<SpotReport> Problems, IReadOnlyList<SpotReport> CeilingBonks,
    IReadOnlyList<RegionReport> BlastOnlyAreas);

/// <summary>A spot where flights met the same event.</summary>
/// <param name="Kind">The event, snake_case.</param>
/// <param name="Count">Flights that met it.</param>
/// <param name="Centre">The mean position, [x, y, z].</param>
/// <param name="Launches">The launches that led there.</param>
/// <param name="Solid">The bounds of the solid most often involved, [min, max], or null.</param>
/// <param name="Marker">The marker number drawn for it, or 0.</param>
public sealed record SpotReport(string Kind, int Count, float[] Centre, IReadOnlyList<string> Launches, float[][]? Solid, int Marker);

/// <summary>A reported region of floor (or one spawn).</summary>
/// <param name="Nodes">How many floor positions it covers.</param>
/// <param name="Sample">A representative position, [x, y, z].</param>
/// <param name="Min">Its lower bound.</param>
/// <param name="Max">Its upper bound.</param>
/// <param name="Marker">The marker number drawn for it, or 0 when it has none.</param>
/// <param name="Escapes">For a trap, the launches whose blast jumps get out of it (empty when none do); null for other regions or when no flights were tried.</param>
public sealed record RegionReport(int Nodes, float[] Sample, float[] Min, float[] Max, int Marker, IReadOnlyList<string>? Escapes = null);
