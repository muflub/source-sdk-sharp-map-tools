//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.AssetDb;

/// <summary>Which models an import renders and records.</summary>
public enum ModelSelection
{
    /// <summary>No models at all.</summary>
    None,

    /// <summary>
    /// Props a map can place: models compiled as static props, plus anything
    /// in a directory whose name starts with <c>props</c>. Leaves out player
    /// and weapon models and cosmetics, which are most of a game's model count
    /// and nothing a level designer places.
    /// </summary>
    Props,

    /// <summary>Every model under <c>models/</c>.</summary>
    All,
}

/// <summary>A progress report from an import, for the host to print.</summary>
/// <param name="Stage">The pass: <c>materials</c>, <c>textures</c> or <c>models</c>.</param>
/// <param name="Done">How many inputs of the pass are finished.</param>
/// <param name="Total">How many inputs the pass has.</param>
public readonly record struct ImportProgress(string Stage, int Done, int Total);

/// <summary>What an import does and how hard it works.</summary>
public sealed record AssetExportOptions
{
    /// <summary>
    /// Gets the longest edge a texture image may have, in pixels. The export
    /// writes the largest stored mip that fits, so no resampling filter is
    /// involved; 0 writes the top mip whatever its size.
    /// </summary>
    /// <remarks>
    /// 512 by default: enough to judge any surface by eye, a quarter of the
    /// disk of 1024, and larger than the resolution a vision model looks at.
    /// </remarks>
    public int MaxTextureSize { get; init; } = 512;

    /// <summary>Gets whether textures are exported at all.</summary>
    public bool Textures { get; init; } = true;

    /// <summary>Gets which models are exported.</summary>
    public ModelSelection Models { get; init; } = ModelSelection.Props;

    /// <summary>Gets the edge length of one rendered model view, in pixels.</summary>
    public int ViewSize { get; init; } = 384;

    /// <summary>
    /// Gets the longest edge of a texture used to skin a model render. Small,
    /// because a 384-pixel view of a whole prop never shows more texels than
    /// this, and the decoded textures are cached for the run.
    /// </summary>
    public int ModelTextureSize { get; init; } = 256;

    /// <summary>
    /// Gets whether assets already in the database with the same content hash
    /// are decoded and rendered again. Off by default, which is what makes
    /// importing a second game that shares the first one's content quick.
    /// </summary>
    public bool Force { get; init; }

    /// <summary>Gets how many files are processed at once.</summary>
    public int MaxParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>Gets where progress goes, or null for nowhere.</summary>
    public IProgress<ImportProgress>? Progress { get; init; }
}
