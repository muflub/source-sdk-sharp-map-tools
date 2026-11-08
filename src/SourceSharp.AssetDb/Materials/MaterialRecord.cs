//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.AssetDb.Materials;

/// <summary>
/// One material as a map author picks it: the name a VMF face or a model
/// names, what shader draws it and which textures it shows.
/// </summary>
/// <remarks>
/// <para>
/// Materials, not textures, are what a map uses: a brush face says
/// <c>BRICK/BRICKWALL001A</c>, which is a <c>.vmt</c>, which names a
/// <c>.vtf</c>. The database keeps both and joins them, so an agent can browse
/// pictures and still come away with the name it has to write.
/// </para>
/// <para>
/// Every path here is lower case, forward-slashed, without the
/// <c>materials/</c> prefix or an extension: the engine matches material
/// names case-blind, so one spelling per material is the honest key.
/// </para>
/// </remarks>
public sealed record MaterialRecord
{
    /// <summary>Gets the material's name, as a VMF or model names it.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the shader, lower case, after any patch include is resolved.</summary>
    public required string Shader { get; init; }

    /// <summary>Gets what the material is for; see <see cref="MaterialUsage"/>.</summary>
    public required string Usage { get; init; }

    /// <summary>Gets the first directory of the path (<c>brick</c>, <c>metal</c>, ...), or empty at the root.</summary>
    public required string Category { get; init; }

    /// <summary>Gets <c>$basetexture</c>, normalised like <see cref="Path"/>.</summary>
    public string? BaseTexture { get; init; }

    /// <summary>Gets <c>$basetexture2</c>, the second layer of a blend.</summary>
    public string? BaseTexture2 { get; init; }

    /// <summary>Gets <c>$bumpmap</c> (or <c>$normalmap</c>).</summary>
    public string? BumpMap { get; init; }

    /// <summary>Gets <c>$detail</c>.</summary>
    public string? Detail { get; init; }

    /// <summary>Gets <c>$envmap</c>, verbatim (often <c>env_cubemap</c>).</summary>
    public string? EnvMap { get; init; }

    /// <summary>Gets <c>%tooltexture</c>, the picture Hammer shows for a material without a base texture.</summary>
    public string? ToolTexture { get; init; }

    /// <summary>Gets <c>$surfaceprop</c>: what it sounds and breaks like.</summary>
    public string? SurfaceProp { get; init; }

    /// <summary>Gets <c>$surfaceprop2</c>, the second layer's surface property.</summary>
    public string? SurfaceProp2 { get; init; }

    /// <summary>Gets <c>%keywords</c>, the authors' own search tags.</summary>
    public string? Keywords { get; init; }

    /// <summary>Gets whether <c>$translucent</c> is set.</summary>
    public bool Translucent { get; init; }

    /// <summary>Gets whether <c>$alphatest</c> is set.</summary>
    public bool AlphaTest { get; init; }

    /// <summary>Gets whether <c>$selfillum</c> is set.</summary>
    public bool SelfIllum { get; init; }

    /// <summary>Gets the <c>include</c> of a patch material, or null for an ordinary one.</summary>
    public string? Include { get; init; }

    /// <summary>Gets every top-level scalar parameter as a JSON object, keys lower case, last one winning.</summary>
    public required string Parameters { get; init; }

    /// <summary>Gets the <c>.vmt</c> text as the content holds it.</summary>
    public required string Vmt { get; init; }

    /// <summary>Gets the mount the file was read from (a VPK or a directory).</summary>
    public required string Source { get; init; }

    /// <summary>
    /// The texture a picture of this material should show: the base texture,
    /// or Hammer's tool texture for materials without one (water, tools).
    /// </summary>
    public string? PreviewTexture => BaseTexture ?? ToolTexture;
}

/// <summary>The values <see cref="MaterialRecord.Usage"/> takes.</summary>
/// <remarks>
/// The classification an agent filters on first: a wall needs a
/// <see cref="Brush"/> material, and the database holds many thousands of
/// model skins that would otherwise drown every search for "metal".
/// </remarks>
public static class MaterialUsage
{
    /// <summary>A world surface: lightmapped shaders, displacement blends.</summary>
    public const string Brush = "brush";

    /// <summary>A model skin: vertex-lit shaders, or anything under <c>models/</c>.</summary>
    public const string Model = "model";

    /// <summary>A decal or overlay.</summary>
    public const string Decal = "decal";

    /// <summary>A tool material: nodraw, clip, trigger, skip and the rest under <c>tools/</c>.</summary>
    public const string Tool = "tool";

    /// <summary>A water surface.</summary>
    public const string Water = "water";

    /// <summary>A skybox face.</summary>
    public const string Sky = "sky";

    /// <summary>Anything else: sprites, UI, particles, effects.</summary>
    public const string Other = "other";
}
