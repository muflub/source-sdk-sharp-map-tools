//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapFormats.Nav;

/// <summary>
/// One box of a hull navigation: a block of free voxels inside one cell,
/// every voxel of it with the same key, its bottom voxel standing on the
/// key's floor.
/// </summary>
/// <param name="X0">The footprint's first voxel column along x, within the cell.</param>
/// <param name="Y0">Along y.</param>
/// <param name="X1">One past its last column along x, within the cell: at most the cell's voxels.</param>
/// <param name="Y1">Along y.</param>
/// <param name="ZLo">The lowest voxel, counted from the grid's floor.</param>
/// <param name="Height">How many voxels up, at least 1.</param>
/// <param name="Key">Its key: an index into the file's keys.</param>
public readonly record struct NavHullBox(byte X0, byte Y0, byte X1, byte Y1, ushort ZLo, ushort Height, uint Key)
{
    /// <summary>The highest voxel.</summary>
    public int ZHi => ZLo + Height - 1;

    /// <summary>Whether the footprint holds a column of the cell.</summary>
    /// <param name="x">The column along x, within the cell.</param>
    /// <param name="y">Along y.</param>
    /// <returns>True when it does.</returns>
    public bool Holds(int x, int y) => x >= X0 && x < X1 && y >= Y0 && y < Y1;
}

/// <summary>
/// What every voxel of a box shares, for the player class: what it is, what
/// its bottom stands on, and, per built width, the ceiling a body of that
/// width meets at a column's centre.
/// </summary>
/// <param name="Flags">Water, ladder, and whether the bottom stands on a walkable floor.</param>
/// <param name="Cost">The traversal cost multiplier in 8.8 fixed point: 256 is 1.0.</param>
/// <param name="FloorZ">The floor's altitude under the bottom voxel when <see cref="NavHullFlags.Grounded"/>; else 0.</param>
/// <param name="Ceilings">
/// Per width, in the file's width order: the lowest altitude a body of that
/// width, centred on a column, meets solid at, with every obstacle open. A
/// body with its feet at <c>z</c> and height <c>h</c> fits exactly when
/// <c>z + h</c> is at most the ceiling (give or take
/// <see cref="NavBrush.Epsilon"/>). Negative infinity when the width does
/// not fit at any height.
/// </param>
/// <param name="ShutFirst">The first of its shut records.</param>
/// <param name="ShutCount">How many shut records it has: one per obstacle that lowers a ceiling when shut.</param>
public sealed record NavHullKey(NavHullFlags Flags, ushort Cost, float FloorZ, IReadOnlyList<float> Ceilings, uint ShutFirst, uint ShutCount)
{
    /// <inheritdoc/>
    public bool Equals(NavHullKey? other) =>
        other is not null && Flags == other.Flags && Cost == other.Cost && FloorZ.Equals(other.FloorZ)
        && ShutFirst == other.ShutFirst && ShutCount == other.ShutCount && Ceilings.SequenceEqual(other.Ceilings);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Flags);
        hash.Add(Cost);
        hash.Add(FloorZ);
        hash.Add(ShutFirst);
        hash.Add(ShutCount);
        foreach (float ceiling in Ceilings)
        {
            hash.Add(ceiling);
        }

        return hash.ToHashCode();
    }
}

/// <summary>A key's ceilings with one obstacle shut (a door closed, a prop at its place) and every other open.</summary>
/// <param name="Obstacle">The obstacle.</param>
/// <param name="Ceilings">Per width, the ceiling with it shut.</param>
public sealed record NavHullShut(uint Obstacle, IReadOnlyList<float> Ceilings)
{
    /// <inheritdoc/>
    public bool Equals(NavHullShut? other) => other is not null && Obstacle == other.Obstacle && Ceilings.SequenceEqual(other.Ceilings);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Obstacle);
        foreach (float ceiling in Ceilings)
        {
            hash.Add(ceiling);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// A jump link for the player: a floor in one box's column and a floor in
/// another's, up to the jump distance apart in a straight line, whose rise
/// is more than a step and at most the jump height (up by jumping, down by
/// dropping).
/// </summary>
/// <param name="BoxA">The lower-numbered box.</param>
/// <param name="BoxB">The other.</param>
/// <param name="AX">The column of A it leaves from, within A's cell, along x.</param>
/// <param name="AY">Along y.</param>
/// <param name="BX">The column of B, within B's cell, along x.</param>
/// <param name="BY">Along y.</param>
/// <param name="Rise">B's floor minus A's.</param>
/// <param name="Direction">The direction from A to B.</param>
/// <param name="Columns">How many columns apart: 1 for a ledge, more across a gap.</param>
public readonly record struct NavHullJump(uint BoxA, uint BoxB, byte AX, byte AY, byte BX, byte BY, float Rise, Nav3dDirection Direction, byte Columns);

/// <summary>A hull navigation in memory, as the writer takes it and the reader gives it back (<see cref="NavHullReader.ToLevel"/>).</summary>
public sealed class NavHullLevel
{
    /// <summary>A voxel's edge, in units.</summary>
    public float VoxelSize { get; init; }

    /// <summary>Voxels along a cell's edge, 1 to <see cref="NavHullFormat.MaxCellVoxels"/>.</summary>
    public int CellVoxels { get; init; }

    /// <summary>The grid's cells west to east.</summary>
    public int Columns { get; init; }

    /// <summary>The grid's cells south to north.</summary>
    public int Rows { get; init; }

    /// <summary>The grid's low corner, level coordinates.</summary>
    public Vec3 Origin { get; init; }

    /// <summary>Voxels up every column, 1 to <see cref="NavHullFormat.MaxColumnVoxels"/>.</summary>
    public int ColumnVoxels { get; init; }

    /// <summary>The least normal z of a walkable floor.</summary>
    public float FloorNormalZ { get; init; }

    /// <summary>The highest step a player walks up without jumping.</summary>
    public float StepHeight { get; init; }

    /// <summary>The highest ledge a jump link climbs, and the deepest it drops.</summary>
    public float JumpHeight { get; init; }

    /// <summary>The farthest a jump link reaches, between column centres.</summary>
    public float JumpDistance { get; init; }

    /// <summary>The widths the ceilings are for, rising, each above 0.</summary>
    public IReadOnlyList<float> Widths { get; init; } = [];

    /// <summary>Per cell (<c>row × columns + column</c>), plus one: cell <c>c</c>'s boxes are <c>[CellStarts[c], CellStarts[c + 1])</c>.</summary>
    public uint[] CellStarts { get; init; } = [0];

    /// <summary>The boxes, cell by cell.</summary>
    public NavHullBox[] Boxes { get; init; } = [];

    /// <summary>The distinct keys the boxes name.</summary>
    public IReadOnlyList<NavHullKey> Keys { get; init; } = [];

    /// <summary>The keys' shut records, each key's together.</summary>
    public IReadOnlyList<NavHullShut> Shuts { get; init; } = [];

    /// <summary>The dynamic obstacles, as the <c>.nav3d</c> names them.</summary>
    public IReadOnlyList<Nav3dObstacle> Obstacles { get; init; } = [];

    /// <summary>The jump links, sorted by box A, then box B, then A's and B's columns.</summary>
    public IReadOnlyList<NavHullJump> Jumps { get; init; } = [];

    /// <summary>The compiled map the navigation was built from, or null.</summary>
    public Nav3dBspBinding? Bsp { get; init; }
}
