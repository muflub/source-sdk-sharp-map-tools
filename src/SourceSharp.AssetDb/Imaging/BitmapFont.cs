//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Collections.Frozen;

namespace SourceSharp.AssetDb.Imaging;

/// <summary>
/// A 5x7 pixel font covering upper-case letters, digits and a little
/// punctuation, for labelling rendered views.
/// </summary>
/// <remarks>
/// <para>
/// The labels are for a vision model reading the image: a panel that says
/// <c>TOP</c> and <c>GRID 64</c> cannot be misread as another view or another
/// scale, which an unlabelled sheet plus a convention in a README can. A
/// system font would make the bytes depend on what the machine has installed,
/// so the glyphs live here, drawn as text.
/// </para>
/// <para>
/// Lower-case input is drawn upper-case; a character with no glyph draws as
/// a space, so a label can never throw.
/// </para>
/// </remarks>
public static class BitmapFont
{
    /// <summary>A glyph's width in font pixels, before scaling.</summary>
    public const int GlyphWidth = 5;

    /// <summary>A glyph's height in font pixels, before scaling.</summary>
    public const int GlyphHeight = 7;

    /// <summary>The advance from one glyph to the next, in font pixels.</summary>
    public const int Advance = GlyphWidth + 1;

    private static readonly FrozenDictionary<char, string> Glyphs = new Dictionary<char, string>
    {
        ['A'] = ".###.#...##...#######...##...##...#",
        ['B'] = "####.#...##...#####.#...##...#####.",
        ['C'] = ".###.#...##....#....#....#...#.###.",
        ['D'] = "####.#...##...##...##...##...#####.",
        ['E'] = "######....#....####.#....#....#####",
        ['F'] = "######....#....####.#....#....#....",
        ['G'] = ".###.#...##....#.####...##...#.####",
        ['H'] = "#...##...##...#######...##...##...#",
        ['I'] = ".###...#....#....#....#....#...###.",
        ['J'] = "..###...#....#....#....#.#..#..##..",
        ['K'] = "#...##..#.#.#..##...#.#..#..#.#...#",
        ['L'] = "#....#....#....#....#....#....#####",
        ['M'] = "#...###.###.#.##.#.##...##...##...#",
        ['N'] = "#...###..##.#.##..###...##...##...#",
        ['O'] = ".###.#...##...##...##...##...#.###.",
        ['P'] = "####.#...##...#####.#....#....#....",
        ['Q'] = ".###.#...##...##...##.#.##..#..##.#",
        ['R'] = "####.#...##...#####.#.#..#..#.#...#",
        ['S'] = ".#####....#.....###.....#....#####.",
        ['T'] = "#####..#....#....#....#....#....#..",
        ['U'] = "#...##...##...##...##...##...#.###.",
        ['V'] = "#...##...##...##...##...#.#.#...#..",
        ['W'] = "#...##...##...##.#.##.#.##.#.#.#.#.",
        ['X'] = "#...##...#.#.#...#...#.#.#...##...#",
        ['Y'] = "#...##...#.#.#...#....#....#....#..",
        ['Z'] = "#####....#...#...#...#...#....#####",
        ['0'] = ".###.#...##..###.#.###..##...#.###.",
        ['1'] = "..#...##....#....#....#....#...###.",
        ['2'] = ".###.#...#....#...#...#...#...#####",
        ['3'] = "#####...#...#.....#.....##...#.###.",
        ['4'] = "...#...##..#.#.#..#.#####...#....#.",
        ['5'] = "######....####.....#....##...#.###.",
        ['6'] = "..##..#...#....####.#...##...#.###.",
        ['7'] = "#####....#...#...#...#....#....#...",
        ['8'] = ".###.#...##...#.###.#...##...#.###.",
        ['9'] = ".###.#...##...#.####....#...#..##..",
        ['.'] = "..........................##...##..",
        ['-'] = "................###................",
        ['+'] = ".......#....#..#####..#....#.......",
        ['/'] = ".........#...#...#...#...#.........",
        [':'] = ".......##...##.........##...##.....",
        ['('] = "...#...#...#....#....#.....#.....#.",
        [')'] = ".#.....#.....#....#....#...#...#...",
    }.ToFrozenDictionary();

    /// <summary>The width a string draws at, in image pixels.</summary>
    /// <param name="text">The text.</param>
    /// <param name="scale">Image pixels per font pixel.</param>
    /// <returns>The width; zero for empty text.</returns>
    public static int Measure(string text, int scale)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length == 0 ? 0 : ((text.Length * Advance) - 1) * scale;
    }

    /// <summary>
    /// A glyph's cells, row by row from the top, <c>#</c> for ink and
    /// <c>.</c> for none: <see cref="GlyphWidth"/> times
    /// <see cref="GlyphHeight"/> characters.
    /// </summary>
    /// <param name="c">The character, upper-cased before the lookup.</param>
    /// <returns>The cells, or null when the font has no glyph for it.</returns>
    public static string? GlyphRows(char c) => Glyphs.GetValueOrDefault(char.ToUpperInvariant(c));

    /// <summary>Gets every character the font draws.</summary>
    public static IEnumerable<char> Characters => Glyphs.Keys;

    /// <summary>Whether a character has a glyph (after upper-casing).</summary>
    /// <param name="c">The character.</param>
    /// <returns>True when it draws as something other than a space.</returns>
    public static bool HasGlyph(char c) => Glyphs.ContainsKey(char.ToUpperInvariant(c));

    /// <summary>
    /// Draws text with its top-left at (<paramref name="x"/>, <paramref name="y"/>),
    /// on a filled backing box one font pixel wider all round, clipped to the
    /// image.
    /// </summary>
    /// <param name="image">The image to draw on.</param>
    /// <param name="text">The text.</param>
    /// <param name="x">The left edge of the first glyph.</param>
    /// <param name="y">The top edge of the glyphs.</param>
    /// <param name="scale">Image pixels per font pixel; at least one.</param>
    /// <param name="ink">The glyph colour.</param>
    /// <param name="backing">The box colour behind the text.</param>
    public static void Draw(RgbaImage image, string text, int x, int y, int scale, Rgba ink, Rgba backing)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);
        if (text.Length == 0)
        {
            return;
        }

        image.FillRect(
            x - scale,
            y - scale,
            Measure(text, scale) + (2 * scale),
            (GlyphHeight + 2) * scale,
            backing);

        for (int i = 0; i < text.Length; i++)
        {
            if (!Glyphs.TryGetValue(char.ToUpperInvariant(text[i]), out string? rows))
            {
                continue;
            }

            int left = x + (i * Advance * scale);
            for (int at = 0; at < rows.Length && at < GlyphWidth * GlyphHeight; at++)
            {
                if (rows[at] != '#')
                {
                    continue;
                }

                int column = at % GlyphWidth;
                int row = at / GlyphWidth;
                image.FillRect(left + (column * scale), y + (row * scale), scale, scale, ink);
            }
        }
    }
}
