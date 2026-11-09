//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Assets;

namespace SourceSharp.MapFormats.Imaging;

/// <summary>
/// What an agent can filter a texture on without looking at it: its average
/// colour and whether it carries transparency.
/// </summary>
/// <param name="AverageR">The mean red over every pixel, rounded half up.</param>
/// <param name="AverageG">The mean green.</param>
/// <param name="AverageB">The mean blue.</param>
/// <param name="HasAlpha">Whether any pixel is less than fully opaque.</param>
public readonly record struct ImageStats(byte AverageR, byte AverageG, byte AverageB, bool HasAlpha)
{
    /// <summary>
    /// Measures an image. Integer sums only, so the answer is exact and the
    /// same everywhere.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <returns>Its statistics.</returns>
    public static ImageStats Measure(RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        long r = 0, g = 0, b = 0;
        bool alpha = false;
        byte[] pixels = image.Pixels;
        for (int at = 0; at < pixels.Length; at += 4)
        {
            r += pixels[at];
            g += pixels[at + 1];
            b += pixels[at + 2];
            alpha |= pixels[at + 3] != 255;
        }

        long count = (long)image.Width * image.Height;
        return new ImageStats(Mean(r, count), Mean(g, count), Mean(b, count), alpha);
    }

    /// <summary>The colour as a <c>#rrggbb</c> string, the form the database stores.</summary>
    public string Hex => $"#{AverageR:x2}{AverageG:x2}{AverageB:x2}";

    private static byte Mean(long sum, long count) => (byte)(((2 * sum) + count) / (2 * count));
}

/// <summary>Picks which mip of a texture to decode for a given size budget.</summary>
public static class MipChoice
{
    /// <summary>
    /// The largest mip whose longer edge fits in <paramref name="maxEdge"/>,
    /// or the smallest mip the file has when none does.
    /// </summary>
    /// <param name="texture">The texture.</param>
    /// <param name="maxEdge">The longest edge allowed, in pixels; zero or less means the top mip.</param>
    /// <returns>The mip level, 0 being the largest.</returns>
    /// <remarks>
    /// Choosing a stored mip instead of resampling keeps the export a pure
    /// decode: the pixels in the PNG are the pixels the engine would draw at
    /// that distance, and no filter of ours has to be argued about.
    /// </remarks>
    public static int For(VtfFile texture, int maxEdge)
    {
        ArgumentNullException.ThrowIfNull(texture);
        int mips = Math.Max(1, texture.MipCount);
        if (maxEdge <= 0)
        {
            return 0;
        }

        for (int mip = 0; mip < mips; mip++)
        {
            if (Math.Max(texture.MipWidth(mip), texture.MipHeight(mip)) <= maxEdge)
            {
                return mip;
            }
        }

        return mips - 1;
    }
}
