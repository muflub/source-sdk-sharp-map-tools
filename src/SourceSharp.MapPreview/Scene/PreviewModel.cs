//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapPreview.Scene;

/// <summary>
/// A studio model as the preview draws it: its level-zero triangles, the
/// material each skin family gives each slot, and its bounds.
/// </summary>
/// <param name="Path">The model path as the map names it, normalised.</param>
/// <param name="Triangles">Model-space triangles, default bodygroups.</param>
/// <param name="SkinMaterials">
/// Per skin family, per slot, the material name (relative to
/// <c>materials/</c>, no extension). Family 0 is always present.
/// </param>
/// <param name="HullMin">The model's collision hull lower corner.</param>
/// <param name="HullMax">The model's collision hull upper corner.</param>
public sealed record PreviewModel(
    string Path,
    IReadOnlyList<StudioTriangle> Triangles,
    IReadOnlyList<IReadOnlyList<string>> SkinMaterials,
    Vec3 HullMin,
    Vec3 HullMax)
{
    /// <summary>
    /// The material a slot draws with in a skin family; an out-of-range family
    /// falls back to family 0, as the engine does.
    /// </summary>
    /// <param name="skin">The skin family.</param>
    /// <param name="slot">The mesh's material slot.</param>
    /// <returns>The material name, or null when the slot has none.</returns>
    public string? MaterialFor(int skin, int slot)
    {
        IReadOnlyList<string> family = skin >= 0 && skin < SkinMaterials.Count ? SkinMaterials[skin] : SkinMaterials[0];
        return slot >= 0 && slot < family.Count ? family[slot] : null;
    }

    /// <summary>Normalises a model path the way the scene keys models: lower case, forward slashes.</summary>
    /// <param name="path">The path as written.</param>
    /// <returns>The key.</returns>
    public static string Key(string path) => path.Replace('\\', '/').Trim().ToLowerInvariant();
}
