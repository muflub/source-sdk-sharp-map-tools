//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.AssetDb;

/// <summary>
/// One input an import passed over or could not read, with why.
/// </summary>
/// <remarks>
/// Nothing a single file does can end an import: a game's content always has
/// a few float-format textures, broken patches and stripped models in it, and
/// a database missing those is still worth having when none at all is not.
/// Each one is recorded instead, in the database's <c>issues</c> table, so a
/// later reader can tell "not in the game" from "in the game, not readable".
/// </remarks>
/// <param name="Kind">What the input was: <c>texture</c>, <c>material</c> or <c>model</c>.</param>
/// <param name="Path">The content path, as the mount spells it.</param>
/// <param name="Reason">What happened, for a person reading the table.</param>
public sealed record ImportIssue(string Kind, string Path, string Reason);
