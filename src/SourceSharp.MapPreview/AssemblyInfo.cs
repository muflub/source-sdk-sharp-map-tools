//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Runtime.CompilerServices;

// Tests reach the renderer's internal stages (rasteriser spans, tile layout)
// directly so each branch gets a fact without building a whole map. The CLI
// gets no such access: anything ssmap needs from the preview is public.
[assembly: InternalsVisibleTo("SourceSharp.Tests")]
