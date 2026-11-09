//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;
using System.Text;

using SourceSharp.MapFormats.Imaging;
using SourceSharp.AssetDb.Textures;
using SourceSharp.MapFormats.Assets;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapTools.Io;

namespace SourceSharp.AssetDb.Materials;

/// <summary>One material's lit preview: its base texture lit through its bump map.</summary>
/// <param name="Material">The material's name.</param>
/// <param name="Sha256">The hash of what the picture depends on: both textures' bytes, the bump kind and <see cref="MaterialPreview.Version"/>.</param>
/// <param name="Image">The PNG, relative to the database's directory.</param>
/// <param name="Width">The PNG's width.</param>
/// <param name="Height">The PNG's height.</param>
public sealed record PreviewRecord(string Material, string Sha256, string Image, int Width, int Height);

/// <summary>
/// Renders lit previews of bump-mapped brush materials: the flat base texture
/// alone shows a wall's colours, the lit preview shows its relief, which is
/// what the eye reads in game.
/// </summary>
/// <remarks>
/// <para>
/// Lighting. One directional light from the upper left of the image and in
/// front of the surface, <c>L = normalize(-0.5, -0.5, 0.7)</c> in tangent
/// space with x to the right of the image, y down it and z out of the
/// surface: the engine's normal maps store y pointing down the texture. A
/// pixel's colour is its base texel times
/// <c>0.3 + 0.7 · factor</c>, clamped to 1.5, where <c>factor</c> is the
/// pixel's response to <c>L</c> divided by the image's mean response.
/// </para>
/// <para>
/// Dividing by the mean keeps the preview's overall brightness the base
/// texture's, so the preview shows relief and never misstates colour.
/// Without it, self-shadowing bump maps authored around a resting level
/// below the flat surface's darken the whole image by a third or more,
/// which reads as a darker material rather than a bumpier one. A uniform
/// bump map therefore reproduces the base texture exactly.
/// </para>
/// <para>
/// A normal map's response is <c>max(0, n · L)</c>. A self-shadowing bump
/// map stores, per pixel, how much light arrives from each of three fixed
/// basis directions (the engine's bump basis); its response is the sum of
/// those amounts weighted by how much of <c>L</c> each basis direction
/// catches. Only products, sums and square roots are used, so the bytes are
/// the same everywhere.
/// </para>
/// <para>
/// The base colour stays the record of the material's colour (its average is
/// taken from the unlit image); the preview exists to be looked at.
/// </para>
/// </remarks>
public static class MaterialPreview
{
    /// <summary>The directory, under the database root, that previews go in.</summary>
    public const string ImageDirectory = "previews";

    /// <summary>
    /// The lighting's version, folded into every preview's hash: changing the
    /// light or the formula must re-render previews an earlier run wrote.
    /// </summary>
    public const int Version = 2;

    private const float Ambient = 0.3f;
    private const float Direct = 0.7f;
    private const float MaxShade = 1.5f;

    /// <summary>Whether a material gets a preview: a brush material with both a base texture and a bump map.</summary>
    /// <param name="material">The material.</param>
    /// <returns>True when a preview is rendered for it.</returns>
    public static bool Wants(MaterialRecord material)
    {
        ArgumentNullException.ThrowIfNull(material);
        return material.Usage == MaterialUsage.Brush && material.BaseTexture is not null && material.BumpMap is not null;
    }

    /// <summary>Indexes the previews a database holds, by material and hash, keyed by image path without <c>.png</c>.</summary>
    /// <param name="previews">The stored previews.</param>
    /// <returns>The index.</returns>
    public static AssetIndex<PreviewRecord> Index(IEnumerable<PreviewRecord> previews) =>
        new(previews, p => p.Material, p => p.Sha256, p => p.Image.EndsWith(".png", StringComparison.Ordinal) ? p.Image[..^4] : p.Image);

    /// <summary>Lights a base texture through a bump map.</summary>
    /// <param name="colour">The base texture.</param>
    /// <param name="bump">The bump map; sampled nearest at the base texture's resolution, whatever its own.</param>
    /// <param name="ssBump">True for a self-shadowing bump map, false for a normal map.</param>
    /// <returns>A new image the base texture's size; alpha is the base texture's.</returns>
    public static RgbaImage Light(RgbaImage colour, RgbaImage bump, bool ssBump)
    {
        ArgumentNullException.ThrowIfNull(colour);
        ArgumentNullException.ThrowIfNull(bump);

        Vec3 light = Unit(new Vec3(-0.5f, -0.5f, 0.7f));
        ReadOnlySpan<Vec3> basis =
        [
            new Vec3(0.81649658f, 0f, 0.57735027f),
            new Vec3(-0.40824830f, 0.70710678f, 0.57735027f),
            new Vec3(-0.40824830f, -0.70710678f, 0.57735027f),
        ];
        float[] catches = new float[3];
        float flat = 0f;
        for (int i = 0; i < 3; i++)
        {
            catches[i] = MathF.Max(0f, Vec3.Dot(basis[i], light));
            flat += 0.57735027f * catches[i];
        }

        float[] factors = new float[colour.Width * colour.Height];
        double sum = 0;
        for (int y = 0; y < colour.Height; y++)
        {
            int by = (int)((long)y * bump.Height / colour.Height);
            for (int x = 0; x < colour.Width; x++)
            {
                int bx = (int)((long)x * bump.Width / colour.Width);
                int b = ((by * bump.Width) + bx) * 4;
                float r = bump.Pixels[b] / 255f, g = bump.Pixels[b + 1] / 255f, bl = bump.Pixels[b + 2] / 255f;

                float factor;
                if (ssBump)
                {
                    factor = flat > 0f ? ((r * catches[0]) + (g * catches[1]) + (bl * catches[2])) / flat : 1f;
                }
                else
                {
                    Vec3 n = new((r * 2f) - 1f, (g * 2f) - 1f, (bl * 2f) - 1f);
                    float length = n.Length();
                    n = length > 0f ? n * (1f / length) : new Vec3(0, 0, 1);
                    factor = MathF.Max(0f, Vec3.Dot(n, light)) / light.Z;
                }

                factors[(y * colour.Width) + x] = factor;
                sum += factor;
            }
        }

        // The image's mean response becomes 1: relief varies, brightness does not.
        float mean = (float)(sum / factors.Length);
        float scale = mean > 0f ? 1f / mean : 1f;

        byte[] output = new byte[colour.Pixels.Length];
        for (int i = 0; i < factors.Length; i++)
        {
            float shade = MathF.Min(MaxShade, Ambient + (Direct * factors[i] * scale));
            int at = i * 4;
            output[at] = Lit(colour.Pixels[at], shade);
            output[at + 1] = Lit(colour.Pixels[at + 1], shade);
            output[at + 2] = Lit(colour.Pixels[at + 2], shade);
            output[at + 3] = colour.Pixels[at + 3];
        }

        return new RgbaImage(colour.Width, colour.Height, output);
    }

    /// <summary>Renders the previews of every material that wants one.</summary>
    /// <param name="content">The mounted game.</param>
    /// <param name="output">The database root.</param>
    /// <param name="materials">The game's materials.</param>
    /// <param name="known">The previews the database holds.</param>
    /// <param name="options">Sizes, parallelism, force, progress.</param>
    /// <param name="cancellationToken">Cancels the pass; images already written stay.</param>
    /// <returns>A record per preview, and the issues, each sorted by material.</returns>
    public static async Task<(IReadOnlyList<PreviewRecord> Previews, IReadOnlyList<ImportIssue> Issues)> RenderAsync(
        IContentFileSystem content,
        IFileSystem output,
        IReadOnlyList<MaterialRecord> materials,
        AssetIndex<PreviewRecord> known,
        AssetExportOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(options);

        List<MaterialRecord> wanted = [.. materials.Where(Wants)];
        ConcurrentBag<PreviewRecord> previews = [];
        ConcurrentBag<ImportIssue> issues = [];
        int done = 0;

        await Parallel.ForEachAsync(
            wanted,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelism),
                CancellationToken = cancellationToken,
            },
            async (material, token) =>
            {
                try
                {
                    PreviewRecord? preview = await RenderOneAsync(content, output, material, known, options, issues, token)
                        .ConfigureAwait(false);
                    if (preview is not null)
                    {
                        previews.Add(preview);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    issues.Add(new ImportIssue("preview", material.Path, e.Message));
                }

                options.Progress?.Report(new ImportProgress("previews", Interlocked.Increment(ref done), wanted.Count));
            }).ConfigureAwait(false);

        return (
            [.. previews.OrderBy(p => p.Material, StringComparer.Ordinal)],
            [.. issues.OrderBy(i => i.Path, StringComparer.Ordinal)]);
    }

    private static async Task<PreviewRecord?> RenderOneAsync(
        IContentFileSystem content,
        IFileSystem output,
        MaterialRecord material,
        AssetIndex<PreviewRecord> known,
        AssetExportOptions options,
        ConcurrentBag<ImportIssue> issues,
        CancellationToken cancellationToken)
    {
        byte[]? baseBytes = await ContentWalk.ReadAsync(
            content, VPath.Create($"materials/{material.BaseTexture}.vtf"), cancellationToken).ConfigureAwait(false);
        byte[]? bumpBytes = await ContentWalk.ReadAsync(
            content, VPath.Create($"materials/{material.BumpMap}.vtf"), cancellationToken).ConfigureAwait(false);
        if (baseBytes is null || bumpBytes is null)
        {
            issues.Add(new ImportIssue(
                "preview",
                material.Path,
                $"no {(baseBytes is null ? material.BaseTexture : material.BumpMap)}.vtf to light it with"));
            return null;
        }

        string sha = ContentWalk.Sha256(Encoding.UTF8.GetBytes(
            $"v{Version}:{ContentWalk.Sha256(baseBytes)}:{ContentWalk.Sha256(bumpBytes)}:{material.SsBump}"));
        PreviewRecord? existing = known.Find(material.Path, sha);
        if (existing is not null && !options.Force &&
            await output.ExistsAsync(VPath.Create(existing.Image), cancellationToken).ConfigureAwait(false))
        {
            return existing;
        }

        RgbaImage? colour = Decode(baseBytes, options.MaxTextureSize);
        RgbaImage? bump = Decode(bumpBytes, options.MaxTextureSize);
        if (colour is null || bump is null)
        {
            issues.Add(new ImportIssue("preview", material.Path, "a texture it needs does not decode"));
            return null;
        }

        bool ssBump = material.SsBump || IsSsBump(bumpBytes);
        RgbaImage lit = Light(colour, bump, ssBump);
        string image = existing?.Image ?? known.ImageLocation($"{ImageDirectory}/{material.Path}", sha) + ".png";
        byte[] png = lit.ToPng();
        await output.ReplaceAsync(
            VPath.Create(image),
            async (stream, token) => await stream.WriteAsync(png, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return new PreviewRecord(material.Path, sha, image, lit.Width, lit.Height);
    }

    private static RgbaImage? Decode(byte[] bytes, int maxEdge)
    {
        try
        {
            VtfFile texture = VtfFile.Parse(bytes);
            return ImageDecoder.CanDecode(texture.Format)
                ? TextureExporter.DecodeLargestPresent(texture, MipChoice.For(texture, maxEdge), out _)
                : null;
        }
        catch (Exception e) when (e is InvalidVtfException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsSsBump(byte[] bytes)
    {
        try
        {
            return (VtfFile.Parse(bytes).Header.Flags & (uint)VtfFlags.SsBump) != 0;
        }
        catch (Exception e) when (e is InvalidVtfException or ArgumentException)
        {
            return false;
        }
    }

    private static Vec3 Unit(Vec3 v) => v * (1f / v.Length());

    private static byte Lit(byte channel, float shade) => (byte)MathF.Min(255f, (channel * shade) + 0.5f);
}
