//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.MapTools.Nav;

/// <summary>
/// One convex piece of a model's collision, in the model's frame (Source
/// units, z up): its corners, and its faces as triangles of corner indices.
/// </summary>
/// <remarks>
/// <para>
/// A static prop collides with the convex pieces of its model's
/// <c>.phy</c> (<see cref="NavModelBounds.Pieces"/>), or with its hull box
/// when it has none (<see cref="Box"/>). The faces are kept, not only the
/// corners, because a face says which side of it is solid: the navigation
/// takes the upward faces as the piece's top
/// (<see cref="NavGeometry.StaticProps"/>), and the floor check takes every
/// face as the piece itself (<see cref="NavFloorCheck"/>).
/// </para>
/// <para>
/// The triangles' winding is not relied on: a face's outward side is the
/// one away from the piece's corners' centroid, which for a convex piece is
/// inside it.
/// </para>
/// </remarks>
public sealed class NavPiece
{
    private readonly Vec3[] _points;
    private readonly int[] _triangles;

    /// <summary>Creates a piece.</summary>
    /// <param name="points">Its corners.</param>
    /// <param name="triangles">Its faces, three corner indices each.</param>
    /// <exception cref="ArgumentException">The triangles are not in threes, or name a corner the piece does not have.</exception>
    public NavPiece(IReadOnlyList<Vec3> points, IReadOnlyList<int> triangles)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(triangles);
        if (triangles.Count % 3 != 0 || triangles.Any(i => i < 0 || i >= points.Count))
        {
            throw new ArgumentException("a piece's faces are triangles of its own corners.", nameof(triangles));
        }

        _points = [.. points];
        _triangles = [.. triangles];
        if (_points.Length > 0)
        {
            Vec3 lo = _points[0];
            Vec3 hi = _points[0];
            foreach (Vec3 p in _points)
            {
                lo = new Vec3(Math.Min(lo.X, p.X), Math.Min(lo.Y, p.Y), Math.Min(lo.Z, p.Z));
                hi = new Vec3(Math.Max(hi.X, p.X), Math.Max(hi.Y, p.Y), Math.Max(hi.Z, p.Z));
            }

            Bounds = (lo, hi);
        }
    }

    /// <summary>The corners.</summary>
    public IReadOnlyList<Vec3> Points => _points;

    /// <summary>The faces, three corner indices each.</summary>
    public IReadOnlyList<int> Triangles => _triangles;

    /// <summary>The box around the corners.</summary>
    public (Vec3 Mins, Vec3 Maxs) Bounds { get; }

    /// <summary>A box as a piece: its eight corners and twelve triangles.</summary>
    /// <param name="mins">The low corner.</param>
    /// <param name="maxs">The high corner.</param>
    /// <returns>The piece.</returns>
    public static NavPiece Box(Vec3 mins, Vec3 maxs)
    {
        Vec3[] corners = new Vec3[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = new Vec3((i & 1) == 0 ? mins.X : maxs.X, (i & 2) == 0 ? mins.Y : maxs.Y, (i & 4) == 0 ? mins.Z : maxs.Z);
        }

        // Each face as two triangles; corner i has x from bit 0, y from bit 1, z from bit 2.
        int[] faces =
        [
            0, 2, 6, 0, 6, 4, // -x
            1, 5, 7, 1, 7, 3, // +x
            0, 4, 5, 0, 5, 1, // -y
            2, 3, 7, 2, 7, 6, // +y
            0, 1, 3, 0, 3, 2, // -z
            4, 6, 7, 4, 7, 5, // +z
        ];
        return new NavPiece(corners, faces);
    }
}
