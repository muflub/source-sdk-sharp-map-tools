//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers;

using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Imaging;
using SourceSharp.MapPreview.Scene;
using SourceSharp.MapTools.Io;
using SourceSharp.MapTools.Materials;

namespace SourceSharp.MapPreview.Materials;

/// <summary>
/// The six faces of a 2D skybox, decoded: right, left, back, front, up, down,
/// in the order the engine names them (<c>rt lf bk ft up dn</c>).
/// </summary>
/// <param name="Faces">The six images; a face that did not load is null and draws the fallback colour.</param>
/// <param name="Fallback">The colour drawn where a face is missing: the average of the faces that loaded, else sky blue.</param>
public sealed record PreviewSky(IReadOnlyList<RgbaImage?> Faces, Rgba Fallback);

/// <summary>
/// Resolves material names to <see cref="PreviewMaterial"/>s by reading VMTs
/// and decoding their base textures from the mounted content.
/// </summary>
/// <remarks>
/// <para>
/// VMT reading goes through <see cref="MaterialFactsReader"/>, the same reader
/// the compilers use, so includes and the cubemap patch materials vbsp writes
/// into the pak resolve exactly as they do for a compile. The base texture is
/// decoded at the largest stored mip whose long edge fits
/// <see cref="MaxTextureEdge"/>, which bounds memory on a big map and matches
/// the size the texture is seen at in a preview image.
/// </para>
/// <para>
/// Decoded textures are cached by path for the library's lifetime: a session
/// re-rendering after an edit pays only for materials it has not seen. The
/// cache belongs to the instance and goes with it, so nothing accumulates
/// across sessions.
/// </para>
/// </remarks>
public sealed class PreviewMaterialLibrary
{
    /// <summary>The default longest texture edge decoded, in pixels.</summary>
    public const int DefaultMaxTextureEdge = 512;

    private readonly IContentFileSystem _content;
    private readonly Dictionary<VPath, (RgbaImage? Image, MaterialProblem Problem)> _textures = [];
    private readonly Dictionary<string, PreviewMaterial> _materials = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a library over mounted content.</summary>
    /// <param name="content">The game content, with the map's pak in front.</param>
    /// <param name="maxTextureEdge">The longest texture edge to decode.</param>
    public PreviewMaterialLibrary(IContentFileSystem content, int maxTextureEdge = DefaultMaxTextureEdge)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTextureEdge, 1);
        _content = content;
        MaxTextureEdge = maxTextureEdge;
    }

    /// <summary>Gets the longest texture edge decoded.</summary>
    public int MaxTextureEdge { get; }

    /// <summary>Resolves every material a scene names, in the scene's order.</summary>
    /// <param name="names">The material names.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>One material per name, at the same index.</returns>
    public async Task<PreviewMaterial[]> ResolveAsync(IReadOnlyList<string> names, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        PreviewMaterial[] result = new PreviewMaterial[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result[i] = await ResolveAsync(names[i], cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Resolves one material.</summary>
    /// <param name="name">The material name as the map spells it.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The material; a missing one draws as the checker.</returns>
    public async Task<PreviewMaterial> ResolveAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_materials.TryGetValue(name, out PreviewMaterial? cached))
        {
            return cached;
        }

        PreviewMaterial material = await LoadAsync(name, cancellationToken).ConfigureAwait(false);
        _materials[name] = material;
        return material;
    }

    /// <summary>Loads a 2D skybox's six faces.</summary>
    /// <param name="skyName">The worldspawn <c>skyname</c>.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The sky.</returns>
    public async Task<PreviewSky> LoadSkyAsync(string? skyName, CancellationToken cancellationToken = default)
    {
        RgbaImage?[] faces = new RgbaImage?[6];
        if (!string.IsNullOrWhiteSpace(skyName))
        {
            string[] suffixes = ["rt", "lf", "bk", "ft", "up", "dn"];
            for (int i = 0; i < 6; i++)
            {
                PreviewMaterial face = await ResolveAsync("skybox/" + skyName + suffixes[i], cancellationToken).ConfigureAwait(false);
                faces[i] = face.IsMissing ? null : face.Texture;
            }
        }

        long r = 0, g = 0, b = 0, n = 0;
        foreach (RgbaImage? face in faces)
        {
            if (face is null)
            {
                continue;
            }

            ImageStats stats = ImageStats.Measure(face);
            r += stats.AverageR;
            g += stats.AverageG;
            b += stats.AverageB;
            n++;
        }

        Rgba fallback = n > 0 ? new Rgba((byte)(r / n), (byte)(g / n), (byte)(b / n)) : new Rgba(150, 180, 215);
        return new PreviewSky(faces, fallback);
    }

    private async Task<PreviewMaterial> LoadAsync(string name, CancellationToken cancellationToken)
    {
        if (name == BspSceneBuilder.MissingModelMaterial)
        {
            return PreviewMaterial.Missing(name, MaterialProblem.MissingModel);
        }

        if (name == BspSceneBuilder.MissingSlotMaterial)
        {
            return PreviewMaterial.Missing(name, MaterialProblem.MissingMaterial);
        }

        MaterialFacts facts = await MaterialFactsReader.ReadAsync(name, _content, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!facts.Found)
        {
            return PreviewMaterial.Missing(name, MaterialProblem.MissingMaterial);
        }

        string shader = facts.ShaderName.ToLowerInvariant();
        float alpha = Math.Clamp(MaterialVarValue.ToFloat(facts.GetVar(MaterialVarNames.Alpha), 1f), 0f, 1f);
        float alphaReference = Math.Clamp(MaterialVarValue.ToFloat(facts.GetVar("$alphatestreference"), 0.5f), 0f, 1f);
        bool unlit = shader.StartsWith("unlit", StringComparison.Ordinal) || shader == "sky";
        bool noCull = MaterialVarValue.IsFlagSet(facts.GetVar("$nocull"));
        Rgba tint = new(255, 255, 255);
        if (MaterialVarValue.TryParseVector(facts.GetVar("$color"), out Vec3 colour))
        {
            tint = ToRgba(colour);
        }

        if (shader == "water")
        {
            // Water has no base texture worth drawing; the fog colour is what a
            // player sees looking into it.
            Rgba fog = MaterialVarValue.TryParseVector(facts.GetVar("$fogcolor"), out Vec3 f) ? ToRgba(f) : new Rgba(40, 70, 80);
            return new PreviewMaterial(name, shader, null, fog, MaterialOpacity.Translucent, alphaReference, 0.6f, false, true, MaterialProblem.None);
        }

        if (facts.BaseTexture is not { } texturePath)
        {
            // A shader with no base texture (some tool and effect materials)
            // draws its tint, so it is visible without pretending to a texture.
            return new PreviewMaterial(name, shader, null, tint, facts.Opacity, alphaReference, alpha, unlit, noCull, MaterialProblem.None);
        }

        (RgbaImage? image, MaterialProblem problem) = await TextureAsync(texturePath, cancellationToken).ConfigureAwait(false);
        if (image is null)
        {
            return PreviewMaterial.Missing(name, problem);
        }

        return new PreviewMaterial(name, shader, image, tint, facts.Opacity, alphaReference, alpha, unlit, noCull, MaterialProblem.None);
    }

    private async Task<(RgbaImage? Image, MaterialProblem Problem)> TextureAsync(VPath path, CancellationToken cancellationToken)
    {
        if (_textures.TryGetValue(path, out (RgbaImage? Image, MaterialProblem Problem) cached))
        {
            return cached;
        }

        (RgbaImage? Image, MaterialProblem Problem) result;
        using (IMemoryOwner<byte>? owner = await _content.ReadAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (owner is null)
            {
                result = (null, MaterialProblem.MissingTexture);
            }
            else
            {
                result = Decode(owner.Memory.ToArray(), MaxTextureEdge);
            }
        }

        _textures[path] = result;
        return result;
    }

    /// <summary>Decodes a VTF at the largest mip that fits an edge budget.</summary>
    /// <param name="bytes">The file.</param>
    /// <param name="maxEdge">The longest edge allowed.</param>
    /// <returns>The image, or the problem that stopped it.</returns>
    internal static (RgbaImage? Image, MaterialProblem Problem) Decode(byte[] bytes, int maxEdge)
    {
        try
        {
            VtfFile vtf = VtfFile.Parse(bytes);
            int mip = MipChoice.For(vtf, maxEdge);
            byte[] rgba = vtf.DecodeToRgba8888(mip);
            return (new RgbaImage(vtf.MipWidth(mip), vtf.MipHeight(mip), rgba), MaterialProblem.None);
        }
        catch (Exception e) when (e is InvalidVtfException or NotSupportedException or ArgumentException)
        {
            return (null, MaterialProblem.UndecodableTexture);
        }
    }

    private static Rgba ToRgba(Vec3 colour) => new(
        (byte)Math.Clamp((int)((colour.X * 255f) + 0.5f), 0, 255),
        (byte)Math.Clamp((int)((colour.Y * 255f) + 0.5f), 0, 255),
        (byte)Math.Clamp((int)((colour.Z * 255f) + 0.5f), 0, 255));
}
