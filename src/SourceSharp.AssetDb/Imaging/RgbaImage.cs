//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.AssetDb.Imaging;

/// <summary>
/// An 8-bit RGBA image: row-major from the top-left, four bytes per pixel.
/// </summary>
/// <remarks>
/// The one image type the database code passes around. It is deliberately a
/// plain buffer with its size, not a drawing library: every pixel the export
/// writes is a decision made in this assembly, which is what keeps the PNGs
/// byte-identical from run to run and machine to machine.
/// </remarks>
public sealed class RgbaImage
{
    /// <summary>Creates an image over existing pixels.</summary>
    /// <param name="width">The width in pixels; positive.</param>
    /// <param name="height">The height in pixels; positive.</param>
    /// <param name="pixels">Exactly <c>width * height * 4</c> bytes, kept, not copied.</param>
    public RgbaImage(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException(
                $"a {width}x{height} image needs {width * height * 4} bytes, not {pixels.Length}",
                nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>Creates an image filled with one colour.</summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="fill">The colour every pixel starts as.</param>
    /// <returns>The image.</returns>
    public static RgbaImage Filled(int width, int height, Rgba fill)
    {
        RgbaImage image = new(width, height, new byte[checked(width * height * 4)]);
        image.FillRect(0, 0, width, height, fill);
        return image;
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the pixels, row-major, RGBA.</summary>
    public byte[] Pixels { get; }

    /// <summary>Reads one pixel.</summary>
    /// <param name="x">The column, from the left.</param>
    /// <param name="y">The row, from the top.</param>
    /// <returns>The colour.</returns>
    public Rgba Get(int x, int y)
    {
        int at = ((y * Width) + x) * 4;
        return new Rgba(Pixels[at], Pixels[at + 1], Pixels[at + 2], Pixels[at + 3]);
    }

    /// <summary>Writes one pixel; coordinates outside the image are ignored.</summary>
    /// <param name="x">The column, from the left.</param>
    /// <param name="y">The row, from the top.</param>
    /// <param name="colour">The colour.</param>
    public void Set(int x, int y, Rgba colour)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            return;
        }

        int at = ((y * Width) + x) * 4;
        Pixels[at] = colour.R;
        Pixels[at + 1] = colour.G;
        Pixels[at + 2] = colour.B;
        Pixels[at + 3] = colour.A;
    }

    /// <summary>Fills a rectangle, clipped to the image.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="colour">The colour.</param>
    public void FillRect(int x, int y, int width, int height, Rgba colour)
    {
        int x0 = Math.Max(0, x);
        int y0 = Math.Max(0, y);
        int x1 = Math.Min(Width, x + width);
        int y1 = Math.Min(Height, y + height);
        for (int row = y0; row < y1; row++)
        {
            for (int column = x0; column < x1; column++)
            {
                int at = ((row * Width) + column) * 4;
                Pixels[at] = colour.R;
                Pixels[at + 1] = colour.G;
                Pixels[at + 2] = colour.B;
                Pixels[at + 3] = colour.A;
            }
        }
    }

    /// <summary>Copies another image in, top-left at (<paramref name="x"/>, <paramref name="y"/>), clipped.</summary>
    /// <param name="source">The image to copy.</param>
    /// <param name="x">Where its left edge lands.</param>
    /// <param name="y">Where its top edge lands.</param>
    public void Blit(RgbaImage source, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(source);
        for (int row = 0; row < source.Height; row++)
        {
            int targetRow = y + row;
            if ((uint)targetRow >= (uint)Height)
            {
                continue;
            }

            for (int column = 0; column < source.Width; column++)
            {
                int targetColumn = x + column;
                if ((uint)targetColumn >= (uint)Width)
                {
                    continue;
                }

                int from = ((row * source.Width) + column) * 4;
                int to = ((targetRow * Width) + targetColumn) * 4;
                Pixels[to] = source.Pixels[from];
                Pixels[to + 1] = source.Pixels[from + 1];
                Pixels[to + 2] = source.Pixels[from + 2];
                Pixels[to + 3] = source.Pixels[from + 3];
            }
        }
    }

    /// <summary>Encodes the image as a PNG.</summary>
    /// <returns>The file bytes.</returns>
    public byte[] ToPng() => PngEncoder.Encode(Pixels, Width, Height);
}

/// <summary>One 8-bit RGBA colour.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
/// <param name="A">Alpha; 255 is opaque.</param>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255);
