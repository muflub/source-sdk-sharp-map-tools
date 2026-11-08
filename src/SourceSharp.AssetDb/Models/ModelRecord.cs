//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.AssetDb.Models;

/// <summary>
/// One model in the database: its size and shape, how it behaves, what it is
/// skinned with and where its rendered views are.
/// </summary>
/// <remarks>
/// <see cref="Path"/> is exactly the value a <c>prop_static</c>'s
/// <c>model</c> key takes, lower-cased: <c>models/props_c17/oildrum001.mdl</c>.
/// Like textures, a model's identity is its path and the hash of its files,
/// so games that share a model share its row and its pictures.
/// </remarks>
public sealed record ModelRecord
{
    /// <summary>Gets the model's path, as a map entity names it.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the SHA-256 of the <c>.mdl</c>, <c>.vvd</c> and <c>.vtx</c> bytes, in that order.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Gets the name compiled into the model.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the header flags.</summary>
    public required int Flags { get; init; }

    /// <summary>Gets whether the model was compiled as a static prop (<c>$staticprop</c>).</summary>
    public bool IsStaticProp => (Flags & StaticPropFlag) != 0;

    /// <summary>The header flag <c>$staticprop</c> sets.</summary>
    public const int StaticPropFlag = 0x10;

    /// <summary>Gets the default surface property.</summary>
    public required string SurfaceProp { get; init; }

    /// <summary>Gets the mass compiled into the header, in kilograms.</summary>
    public required float Mass { get; init; }

    /// <summary>Gets the movement hull the header declares (often zero for props).</summary>
    public required (Vec3 Min, Vec3 Max) Hull { get; init; }

    /// <summary>Gets the view bounds the header declares.</summary>
    public required (Vec3 Min, Vec3 Max) View { get; init; }

    /// <summary>Gets the box around the drawn level-of-detail-zero triangles: the model's real extent.</summary>
    public required (Vec3 Min, Vec3 Max) Mesh { get; init; }

    /// <summary>Gets the bone count; one for nearly every static prop.</summary>
    public required int Bones { get; init; }

    /// <summary>Gets the sequence count; more than one means the model animates.</summary>
    public required int Sequences { get; init; }

    /// <summary>Gets the body part count.</summary>
    public required int BodyParts { get; init; }

    /// <summary>Gets the level-of-detail count the VTX carries.</summary>
    public required int Lods { get; init; }

    /// <summary>Gets the level-of-detail-zero triangles drawn in the renders.</summary>
    public required int Triangles { get; init; }

    /// <summary>Gets the level-of-detail-zero vertex count.</summary>
    public required int Vertices { get; init; }

    /// <summary>Gets the skin family count; at least one.</summary>
    public required int Skins { get; init; }

    /// <summary>Gets whether a <c>.phy</c> collision model ships beside it.</summary>
    public required bool HasCollision { get; init; }

    /// <summary>Gets <c>prop_data</c>'s <c>base</c> (e.g. <c>Metal.Medium</c>), or null when it has none.</summary>
    public string? PropData { get; init; }

    /// <summary>Gets the model's keyvalue text verbatim.</summary>
    public required string KeyValues { get; init; }

    /// <summary>Gets the grid step drawn in the axis views, in world units.</summary>
    public required int GridStep { get; init; }

    /// <summary>Gets the directory the rendered views are in, relative to the database's directory.</summary>
    public required string ImageDirectory { get; init; }

    /// <summary>Gets the materials each skin family shows.</summary>
    public required IReadOnlyList<ModelMaterial> Materials { get; init; }

    /// <summary>Gets the mount this game read the <c>.mdl</c> from.</summary>
    public required string Source { get; init; }

    /// <summary>Where one view's image is.</summary>
    /// <param name="view">The view.</param>
    /// <returns>The path, relative to the database's directory.</returns>
    public string ImageFor(ModelView view) => $"{ImageDirectory}/{view.ToString().ToLowerInvariant()}.png";

    /// <summary>Gets where the sheet of all six views is.</summary>
    public string SheetImage => $"{ImageDirectory}/{ModelExporter.SheetFileName}";
}

/// <summary>One material slot of one skin family of a model.</summary>
/// <param name="Skin">The skin family, 0 being the default.</param>
/// <param name="Slot">The slot (the column of the skin table).</param>
/// <param name="Texture">The texture name the model file lists for the slot.</param>
/// <param name="Material">The material it resolves to through the model's search paths, or null when none does.</param>
public sealed record ModelMaterial(int Skin, int Slot, string Texture, string? Material);

/// <summary>What the database knows of a model already exported, for reuse.</summary>
/// <param name="Path">The model's path.</param>
/// <param name="Sha256">The hash of its files.</param>
/// <param name="ImageDirectory">Where its views are.</param>
public sealed record KnownModel(string Path, string Sha256, string ImageDirectory);
