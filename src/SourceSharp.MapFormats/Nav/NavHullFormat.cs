//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

namespace SourceSharp.MapFormats.Nav;

/// <summary>
/// The constants of the <c>.navhull</c> player-hull navigation file: its
/// magic, version, section tags and record sizes.
/// </summary>
/// <remarks>
/// <para>
/// The file is specified in <c>docs/navhull-format.md</c>, written so a
/// reader in another language can be built from it alone; this class is
/// the C# spelling of the same numbers. Every multi-byte value is
/// little-endian and every section starts on a four-byte boundary, as in
/// the <c>.nav3d</c> file, whose envelope it shares.
/// </para>
/// <para>
/// <b>Why a second file.</b> A <c>.nav3d</c> leaf stores exact clearance
/// for every agent size, which changes from one voxel column to the next
/// wherever a wall is within reach, so its leaves barely merge. A jump map
/// holds so much open space that at 16-unit voxels its <c>.nav3d</c> would
/// be about 4.5 GB. A game whose agents are one body (TF2's 48-wide player,
/// standing or crouched) needs far less: per built width, the lowest
/// ceiling over the body's footprint at a column's centre. Keyed on that,
/// runs of voxels are long and neighbouring columns agree, so they merge
/// into boxes, and the same map fits in a few hundred thousand of them.
/// </para>
/// </remarks>
public static class NavHullFormat
{
    /// <summary>The file's eight magic bytes: <c>SSNAVHUL</c>.</summary>
    public static ReadOnlySpan<byte> Magic => "SSNAVHUL"u8;

    /// <summary>The version this build reads and writes.</summary>
    public const int Version = 1;

    /// <summary>The file extension <c>ssmap nav3d -hull</c> writes beside the map.</summary>
    public const string Extension = ".navhull";

    /// <summary>The envelope in front of the image: magic, version, codec, the image's length and the stored length (the <c>.nav3d</c> envelope's layout).</summary>
    public const int EnvelopeBytes = 24;

    /// <summary>The image header's size in this version; the header records its own size so a later one may grow.</summary>
    public const int HeaderBytes = 96;

    /// <summary>One entry of the section directory: tag, zero, offset, length.</summary>
    public const int DirectoryEntryBytes = 16;

    /// <summary>The most widths a file is built for.</summary>
    public const int MaxWidths = 16;

    /// <summary>The most voxels along a cell's edge: a box's footprint corners are bytes.</summary>
    public const int MaxCellVoxels = 128;

    /// <summary>The most voxels up a column: a box's low voxel and height are 16 bits.</summary>
    public const int MaxColumnVoxels = ushort.MaxValue;

    /// <summary>A string reference that is absent.</summary>
    public const uint NoString = 0xFFFFFFFFu;

    /// <summary>Section: the string table (NUL-terminated UTF-8; offset 0 is the empty string).</summary>
    public const string StringsTag = "STRS";

    /// <summary>Section: the built widths, <c>float32</c> each, rising.</summary>
    public const string WidthsTag = "WDTH";

    /// <summary>Section: <c>cells + 1</c> <c>uint32</c> starts: cell <c>c</c>'s boxes are <c>[CELB[c], CELB[c+1])</c>.</summary>
    public const string CellBoxesTag = "CELB";

    /// <summary>Section: one <see cref="BoxRecordBytes"/>-byte record per box.</summary>
    public const string BoxesTag = "BOXS";

    /// <summary>Section: one key record per distinct key (<see cref="KeyRecordBytes"/>).</summary>
    public const string KeysTag = "KEYS";

    /// <summary>Section: one shut record per (key, obstacle) pair (<see cref="ShutRecordBytes"/>).</summary>
    public const string ShutsTag = "SHUT";

    /// <summary>Section: one 48-byte record per dynamic obstacle, the <c>.nav3d</c> <c>DYNO</c> layout.</summary>
    public const string ObstaclesTag = "DYNO";

    /// <summary>Section: one <see cref="JumpRecordBytes"/>-byte record per jump link.</summary>
    public const string JumpsTag = "JUMP";

    /// <summary>Section, optional: the compiled map the navigation was built from (<see cref="Nav3dBspBinding"/>).</summary>
    public const string BspBindingTag = "BSPB";

    /// <summary>A box: footprint corners in the cell (four bytes), low voxel and height (two <c>uint16</c>), key (<c>uint32</c>).</summary>
    public const int BoxRecordBytes = 12;

    /// <summary>A jump link: two boxes, their columns within their cells, the rise, the direction and the columns apart.</summary>
    public const int JumpRecordBytes = 20;

    /// <summary>An obstacle record: the <c>.nav3d</c> <c>DYNO</c> record.</summary>
    public const int ObstacleRecordBytes = Nav3dFormat.ObstacleRecordBytes;

    /// <summary>A key record's size for a number of widths: flags, cost, floor, shut range, then a ceiling per width.</summary>
    /// <param name="widths">The file's width count.</param>
    /// <returns>The size in bytes.</returns>
    public static int KeyRecordBytes(int widths) => 16 + (4 * widths);

    /// <summary>A shut record's size for a number of widths: the obstacle, then a ceiling per width.</summary>
    /// <param name="widths">The file's width count.</param>
    /// <returns>The size in bytes.</returns>
    public static int ShutRecordBytes(int widths) => 4 + (4 * widths);
}

/// <summary>A box key's flags: what the box's voxels are and what its bottom stands on, for the player class.</summary>
[Flags]
public enum NavHullFlags : ushort
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The voxels are water.</summary>
    Water = 1,

    /// <summary>The voxels are a ladder a climbing player's origin can be in.</summary>
    Ladder = 2,

    /// <summary>The bottom voxel stands on solid for the player: <see cref="NavHullKey.FloorZ"/> is meaningful.</summary>
    Grounded = 4,

    /// <summary>The floor under the bottom voxel is walkable for the player (grounded, and not too steep).</summary>
    Walkable = 8,

    /// <summary>
    /// The voxels are free of every solid but a player clip: open to a point
    /// (a sight line, a rocket), closed to the player, so every ceiling is
    /// negative infinity.
    /// </summary>
    PlayerBlocked = 16,
}
