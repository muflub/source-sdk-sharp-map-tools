//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Concurrent;
using System.Globalization;

using SourceSharp.AssetDb.Imaging;
using SourceSharp.AssetDb.Materials;
using SourceSharp.MapFormats.Assets;
using SourceSharp.MapTools.Io;

namespace SourceSharp.AssetDb.Textures;

/// <summary>
/// Exports every texture a game can see as a PNG under
/// <c>textures/&lt;name&gt;.png</c> and describes each in a
/// <see cref="TextureRecord"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each texture is independent, so the walk is parallel; the result is sorted
/// by path afterwards, which makes it the same however the work interleaved.
/// </para>
/// <para>
/// A texture the database already holds with the same content hash, and
/// whose PNG is still on disk, is not decoded again: its record is reused.
/// That is what makes a second game cheap when it mounts the first one's
/// archives, and a re-import of an unchanged game nearly free.
/// </para>
/// </remarks>
public static class TextureExporter
{
    /// <summary>The directory, under the database root, that texture images go in.</summary>
    public const string ImageDirectory = "textures";

    /// <summary>Runs the export.</summary>
    /// <param name="content">The mounted game.</param>
    /// <param name="output">The database root, where the images are written.</param>
    /// <param name="known">What the database already holds.</param>
    /// <param name="options">Sizes, parallelism, progress.</param>
    /// <param name="cancellationToken">Cancels the export; images already written stay.</param>
    /// <returns>A record per exported or reused texture, and the issues, each sorted by path.</returns>
    public static async Task<(IReadOnlyList<TextureRecord> Textures, IReadOnlyList<ImportIssue> Issues)> ExportAsync(
        IContentFileSystem content,
        IFileSystem output,
        AssetIndex<TextureRecord> known,
        AssetExportOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(options);

        List<VPath> paths = await ContentWalk.ListAsync(content, "materials", "*.vtf", cancellationToken)
            .ConfigureAwait(false);

        ConcurrentBag<TextureRecord> records = [];
        ConcurrentBag<ImportIssue> issues = [];
        int done = 0;

        await Parallel.ForEachAsync(
            paths,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelism),
                CancellationToken = cancellationToken,
            },
            async (path, token) =>
            {
                try
                {
                    TextureRecord? record = await ExportOneAsync(content, output, known, options, path, issues, token)
                        .ConfigureAwait(false);
                    if (record is not null)
                    {
                        records.Add(record);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    issues.Add(new ImportIssue("texture", path.Value, e.Message));
                }

                options.Progress?.Report(new ImportProgress("textures", Interlocked.Increment(ref done), paths.Count));
            }).ConfigureAwait(false);

        return (
            [.. records.OrderBy(r => r.Path, StringComparer.Ordinal)],
            [.. issues.OrderBy(i => i.Path, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Indexes the textures a database holds for <see cref="ExportAsync"/>,
    /// keyed for the image-location rule by image path without its
    /// <c>.png</c>, which is the form the export asks about.
    /// </summary>
    /// <param name="textures">The stored textures.</param>
    /// <returns>The index.</returns>
    public static AssetIndex<TextureRecord> Index(IEnumerable<TextureRecord> textures) =>
        new(textures, t => t.Path, t => t.Sha256, t => WithoutPng(t.Image));

    private static string WithoutPng(string image) =>
        image.EndsWith(".png", StringComparison.Ordinal) ? image[..^4] : image;

    /// <summary>The texture key of a VTF's content path: lower case, no prefix, no extension.</summary>
    /// <param name="contentPath">The path as the mount spells it.</param>
    /// <returns>The key.</returns>
    public static string TextureName(string contentPath) => MaterialReader.Normalise(contentPath, ".vtf");

    /// <summary>The header's flag bits, named, smallest bit first, comma-separated.</summary>
    /// <param name="flags">The raw header flags.</param>
    /// <returns>The names; empty for no flags.</returns>
    /// <remarks>
    /// A bit with no name in <see cref="VtfFlags"/> is written as its hex
    /// value, so no information is dropped for a flag this port does not know.
    /// </remarks>
    public static string FlagNames(uint flags)
    {
        List<string> names = [];
        for (int bit = 0; bit < 32; bit++)
        {
            uint value = 1u << bit;
            if ((flags & value) == 0)
            {
                continue;
            }

            names.Add(Enum.GetName((VtfFlags)value) ?? "0x" + value.ToString("x8", CultureInfo.InvariantCulture));
        }

        return string.Join(',', names);
    }

    /// <summary>
    /// Decodes <paramref name="mip"/>, or the largest smaller mip the file
    /// actually holds.
    /// </summary>
    /// <param name="texture">The texture.</param>
    /// <param name="mip">The mip wanted.</param>
    /// <param name="failure">Why the wanted mip failed, when every mip did.</param>
    /// <returns>The image, or null when no mip from <paramref name="mip"/> down decodes.</returns>
    /// <remarks>
    /// Some shipped textures are truncated: the header declares a 512-pixel
    /// top mip but the file stops after the 64-pixel one. The engine draws
    /// what is there, and so does the export; the record's image size says
    /// which mip that was.
    /// </remarks>
    public static RgbaImage? DecodeLargestPresent(VtfFile texture, int mip, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(texture);

        failure = null;
        int mips = Math.Max(1, texture.MipCount);
        for (int level = Math.Max(0, mip); level < mips; level++)
        {
            try
            {
                return new RgbaImage(texture.MipWidth(level), texture.MipHeight(level), texture.DecodeToRgba8888(level));
            }
            catch (Exception e) when (e is NotSupportedException or InvalidDataException or InvalidVtfException
                                          or ArgumentException)
            {
                failure ??= e.Message;
            }
        }

        return null;
    }

    private static async Task<TextureRecord?> ExportOneAsync(
        IContentFileSystem content,
        IFileSystem output,
        AssetIndex<TextureRecord> known,
        AssetExportOptions options,
        VPath path,
        ConcurrentBag<ImportIssue> issues,
        CancellationToken cancellationToken)
    {
        byte[]? bytes = await ContentWalk.ReadAsync(content, path, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            issues.Add(new ImportIssue("texture", path.Value, "listed by the mount but not readable"));
            return null;
        }

        ContentSource? source = await content.ResolveAsync(path, cancellationToken).ConfigureAwait(false);
        string mount = source?.Mount ?? string.Empty;
        string name = TextureName(path.Value);
        string sha = ContentWalk.Sha256(bytes);

        TextureRecord? existing = known.Find(name, sha);
        if (existing is not null && !options.Force &&
            await output.ExistsAsync(VPath.Create(existing.Image), cancellationToken).ConfigureAwait(false))
        {
            return existing with { Source = mount };
        }

        VtfFile texture;
        try
        {
            texture = VtfFile.Parse(bytes);
        }
        catch (Exception e) when (e is InvalidVtfException or ArgumentException)
        {
            issues.Add(new ImportIssue("texture", path.Value, $"not a readable VTF: {e.Message}"));
            return null;
        }

        if (!ImageDecoder.CanDecode(texture.Format))
        {
            issues.Add(new ImportIssue("texture", path.Value, $"format {texture.Format} has no decoder"));
            return null;
        }

        RgbaImage? image = DecodeLargestPresent(texture, MipChoice.For(texture, options.MaxTextureSize), out string? failure);
        if (image is null)
        {
            issues.Add(new ImportIssue("texture", path.Value, $"undecodable as {texture.Format}: {failure}"));
            return null;
        }

        ImageStats stats = ImageStats.Measure(image);
        string location = existing?.Image ??
            known.ImageLocation($"{ImageDirectory}/{name}", sha) + ".png";
        byte[] png = image.ToPng();
        await output.ReplaceAsync(
            VPath.Create(location),
            async (stream, token) => await stream.WriteAsync(png, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        return new TextureRecord
        {
            Path = name,
            Sha256 = sha,
            Width = texture.Width,
            Height = texture.Height,
            Format = texture.Format.ToString(),
            Mips = texture.MipCount,
            Frames = texture.FrameCount,
            Flags = FlagNames(texture.Header.Flags),
            Reflectivity = (texture.Reflectivity.X, texture.Reflectivity.Y, texture.Reflectivity.Z),
            AverageColour = stats.Hex,
            HasAlpha = stats.HasAlpha,
            Image = location,
            ImageWidth = image.Width,
            ImageHeight = image.Height,
            Source = mount,
        };
    }
}
