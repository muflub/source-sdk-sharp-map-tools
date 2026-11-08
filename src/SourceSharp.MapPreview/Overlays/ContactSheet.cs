//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Imaging;

namespace SourceSharp.MapPreview.Overlays;

/// <summary>
/// Lays views out in a grid on one image, so an agent reads a whole map's
/// standard views as a single picture.
/// </summary>
/// <remarks>
/// One image of about 1536 by 800 pixels costs a vision model far fewer
/// tokens than six separate ones, and keeps the views side by side for
/// comparison. Each tile already carries its own label, so the sheet adds
/// only the grid.
/// </remarks>
public static class ContactSheet
{
    /// <summary>The gap between tiles, and around the edge, in pixels.</summary>
    public const int Gap = 4;

    /// <summary>The colour between tiles.</summary>
    public static Rgba Border => new(24, 26, 30);

    /// <summary>Lays tiles out in rows.</summary>
    /// <param name="tiles">The tiles, all the same size, in reading order.</param>
    /// <param name="columns">Tiles per row.</param>
    /// <returns>The sheet.</returns>
    public static RgbaImage Compose(IReadOnlyList<RgbaImage> tiles, int columns)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        if (tiles.Count == 0)
        {
            throw new ArgumentException("a sheet needs at least one tile", nameof(tiles));
        }

        int width = tiles[0].Width, height = tiles[0].Height;
        if (tiles.Any(t => t.Width != width || t.Height != height))
        {
            throw new ArgumentException("every tile must be the same size", nameof(tiles));
        }

        int used = Math.Min(columns, tiles.Count);
        int rows = (tiles.Count + columns - 1) / columns;
        RgbaImage sheet = RgbaImage.Filled(
            Gap + (used * (width + Gap)),
            Gap + (rows * (height + Gap)),
            Border);
        for (int i = 0; i < tiles.Count; i++)
        {
            sheet.Blit(tiles[i], Gap + ((i % columns) * (width + Gap)), Gap + ((i / columns) * (height + Gap)));
        }

        return sheet;
    }
}
