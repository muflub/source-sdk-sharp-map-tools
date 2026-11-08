//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Imaging;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapPreview.Materials;

/// <summary>Why a material draws as the missing checker.</summary>
public enum MaterialProblem
{
    /// <summary>Nothing is wrong.</summary>
    None,

    /// <summary>No VMT by that name in the mounted content.</summary>
    MissingMaterial,

    /// <summary>The VMT names a base texture the content does not have.</summary>
    MissingTexture,

    /// <summary>The base texture exists but could not be decoded.</summary>
    UndecodableTexture,

    /// <summary>A model referenced a model file that did not load.</summary>
    MissingModel,
}

/// <summary>
/// A material reduced to what the preview draws: a base texture (or a flat
/// colour), how it handles alpha, and whether light affects it.
/// </summary>
/// <param name="Name">The material name as the map spells it.</param>
/// <param name="Shader">The shader name, lower case; empty when the material was not found.</param>
/// <param name="Texture">The decoded base texture, or null to draw <paramref name="Colour"/>.</param>
/// <param name="Colour">The flat colour used when there is no texture, and the tint over one.</param>
/// <param name="Opacity">Opaque, alpha-tested or translucent.</param>
/// <param name="AlphaReference">The alpha-test cut-off, 0..1.</param>
/// <param name="Alpha">The material's overall alpha (<c>$alpha</c>), 0..1.</param>
/// <param name="Unlit">Whether the surface ignores light (unlit shaders).</param>
/// <param name="NoCull">Whether back faces draw too.</param>
/// <param name="Problem">Why it draws as the checker, if it does.</param>
public sealed record PreviewMaterial(
    string Name,
    string Shader,
    RgbaImage? Texture,
    Rgba Colour,
    MaterialOpacity Opacity,
    float AlphaReference,
    float Alpha,
    bool Unlit,
    bool NoCull,
    MaterialProblem Problem)
{
    /// <summary>The checker's two colours: the engine's missing-texture magenta and black.</summary>
    public static Rgba CheckerA => new(255, 0, 255);

    /// <summary>The checker's second colour.</summary>
    public static Rgba CheckerB => new(0, 0, 0);

    /// <summary>The checker texture's edge, in pixels: two by two squares, one repeat per texture.</summary>
    public const int CheckerSize = 2;

    /// <summary>A material that draws as the magenta and black checker, for a problem.</summary>
    /// <param name="name">The material name.</param>
    /// <param name="problem">What was wrong.</param>
    /// <returns>The material.</returns>
    public static PreviewMaterial Missing(string name, MaterialProblem problem)
    {
        byte[] pixels = new byte[CheckerSize * CheckerSize * 4];
        RgbaImage checker = new(CheckerSize, CheckerSize, pixels);
        checker.Set(0, 0, CheckerA);
        checker.Set(1, 0, CheckerB);
        checker.Set(0, 1, CheckerB);
        checker.Set(1, 1, CheckerA);
        return new PreviewMaterial(name, string.Empty, checker, new Rgba(255, 255, 255), MaterialOpacity.Opaque, 0.5f, 1f, false, false, problem);
    }

    /// <summary>Whether the material draws as the checker.</summary>
    public bool IsMissing => Problem != MaterialProblem.None;

    /// <summary>
    /// Samples the texture at a coordinate, wrapping, with nearest filtering
    /// from a mip chosen at load time; returns the flat colour when there is
    /// no texture.
    /// </summary>
    /// <param name="u">Across, in texture repeats.</param>
    /// <param name="v">Down, in texture repeats.</param>
    /// <returns>The texel.</returns>
    public Rgba Sample(float u, float v)
    {
        if (Texture is null)
        {
            return Colour;
        }

        int w = Texture.Width;
        int h = Texture.Height;
        int x = (int)MathF.Floor(u * w) % w;
        int y = (int)MathF.Floor(v * h) % h;
        if (x < 0)
        {
            x += w;
        }

        if (y < 0)
        {
            y += h;
        }

        return Texture.Get(x, y);
    }

    /// <summary>The flat colour as linear light, for code that has no texel to sample.</summary>
    /// <param name="space">The colour tables.</param>
    /// <returns>The colour.</returns>
    public Vec3 ColourLinear(Render.ColorSpace space) =>
        new(space.ToLinear(Colour.R), space.ToLinear(Colour.G), space.ToLinear(Colour.B));
}
