//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.AssetDb.Textures;

/// <summary>
/// One texture in the database: the facts from its VTF header, what its
/// pixels look like on average, and the PNG it was exported as.
/// </summary>
/// <remarks>
/// A texture's identity is its path AND the hash of its bytes. Two games that
/// mount the same archive (TF2 mounts Half-Life 2's) see the same file and
/// share one row and one PNG; a game that ships a different file at the same
/// path gets a row and a PNG of its own.
/// </remarks>
public sealed record TextureRecord
{
    /// <summary>Gets the texture's name: lower case, no <c>materials/</c>, no <c>.vtf</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the lower-case hex SHA-256 of the VTF file.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Gets the top mip's width.</summary>
    public required int Width { get; init; }

    /// <summary>Gets the top mip's height.</summary>
    public required int Height { get; init; }

    /// <summary>Gets the stored pixel format, as the enum spells it (<c>Dxt1</c>, <c>Bgra8888</c>, ...).</summary>
    public required string Format { get; init; }

    /// <summary>Gets how many mips the file carries.</summary>
    public required int Mips { get; init; }

    /// <summary>Gets how many animation frames the file carries.</summary>
    public required int Frames { get; init; }

    /// <summary>Gets the header flags, named, comma-separated, smallest bit first.</summary>
    public required string Flags { get; init; }

    /// <summary>Gets the header's reflectivity, the colour the light compiler bounces.</summary>
    public required (float R, float G, float B) Reflectivity { get; init; }

    /// <summary>
    /// Gets what the texture holds: <see cref="TextureKind.Color"/>,
    /// <see cref="TextureKind.Normal"/> or <see cref="TextureKind.SsBump"/>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>Gets the exported image's average colour as <c>#rrggbb</c>, or null when no image was written.</summary>
    public string? AverageColour { get; init; }

    /// <summary>Gets whether any exported pixel is less than fully opaque.</summary>
    public required bool HasAlpha { get; init; }

    /// <summary>
    /// Gets the PNG, relative to the database's directory, or null for a
    /// bump map: its pixels are directions, not colours, and it is shown
    /// through the lit preview of the material that uses it instead.
    /// </summary>
    public string? Image { get; init; }

    /// <summary>Gets the PNG's width (the exported mip's), or 0 with no image.</summary>
    public required int ImageWidth { get; init; }

    /// <summary>Gets the PNG's height, or 0 with no image.</summary>
    public required int ImageHeight { get; init; }

    /// <summary>Gets the mount this game read the file from.</summary>
    public required string Source { get; init; }
}

/// <summary>The values <see cref="TextureRecord.Kind"/> takes.</summary>
public static class TextureKind
{
    /// <summary>A picture: base colour, detail, decal, sky, mask.</summary>
    public const string Color = "color";

    /// <summary>A tangent-space normal map.</summary>
    public const string Normal = "normal";

    /// <summary>A self-shadowing bump map: per-pixel light from three fixed directions.</summary>
    public const string SsBump = "ssbump";
}
