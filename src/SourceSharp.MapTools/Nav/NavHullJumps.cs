//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Nav;

namespace SourceSharp.MapTools.Nav;

/// <summary>
/// The player's jump links over a hull navigation's boxes: the rule the
/// <c>.nav3d</c> link applies to its leaves, column by column, for the
/// player class.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule.</b> From each column of a walkable box, straight east,
/// north, west and south up to the jump distance: a walkable box in a column
/// there whose floor rises more than a step (one column away) and at most
/// the jump height, either way, is a link, when the jumper's path is open:
/// the take-off and landing columns free for the player from their bottoms
/// up to the higher floor's bottom voxel, and every column between free at
/// that voxel and without a floor within a step of either end (else it is
/// a walk, not a jump across a gap). An empty cell ends the search that way.
/// </para>
/// <para>
/// <b>Deterministic.</b> Boxes are searched in chunks on the build's
/// threads; each link is keyed by its boxes and columns, the lower box
/// first, and the list is sorted by that key with duplicates (the same link
/// seen from both ends) dropped, so it is the same at any thread count.
/// </para>
/// </remarks>
public static class NavHullJumps
{
    /// <summary>How many take-off boxes one task searches.</summary>
    private const int Chunk = 1024;

    /// <summary>Finds a hull navigation's jump links.</summary>
    /// <param name="nav">The navigation, read without its links (any it has are ignored).</param>
    /// <param name="maxDegree">The most threads to search on.</param>
    /// <param name="cancellationToken">Cancels the search between boxes.</param>
    /// <returns>The links, sorted by box A, box B, then their columns.</returns>
    public static IReadOnlyList<NavHullJump> Find(NavHullReader nav, int maxDegree, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nav);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegree, 1);
        int reach = (int)Math.Floor((nav.JumpDistance / (double)nav.VoxelSize) + 1e-9);
        int chunks = (nav.BoxCount + Chunk - 1) / Chunk;
        List<NavHullJump>[] found = new List<NavHullJump>[chunks];
        ParallelOptions parallel = new() { MaxDegreeOfParallelism = maxDegree, CancellationToken = cancellationToken };
        System.Threading.Tasks.Parallel.For(0, chunks, parallel, k =>
        {
            List<NavHullJump> seen = [];
            for (int box = k * Chunk; box < Math.Min(nav.BoxCount, (k + 1) * Chunk); box++)
            {
                TakeOff(nav, box, reach, seen);
            }

            found[k] = seen;
        });

        List<NavHullJump> all = [.. found.SelectMany(f => f)];
        all.Sort((p, q) => Key(p).CompareTo(Key(q)));
        List<NavHullJump> links = [];
        foreach (NavHullJump jump in all)
        {
            if (links.Count == 0 || Key(links[^1]) != Key(jump))
            {
                links.Add(jump);
            }
        }

        return links;
    }

    private static (uint, uint, byte, byte, byte, byte) Key(NavHullJump j) => (j.BoxA, j.BoxB, j.AX, j.AY, j.BX, j.BY);

    /// <summary>Every jump from one box's columns.</summary>
    private static void TakeOff(NavHullReader nav, int box, int reach, List<NavHullJump> seen)
    {
        if ((nav.Flags(box) & NavHullFlags.Walkable) == 0 || reach < 1)
        {
            return;
        }

        int n = nav.CellVoxels;
        NavHullBox from = nav.Box(box);
        int cell = nav.BoxCell(box);
        float fa = nav.FloorZ(box);
        for (int y = from.Y0; y < from.Y1; y++)
        {
            for (int x = from.X0; x < from.X1; x++)
            {
                int gx = ((cell % nav.Columns) * n) + x;
                int gy = ((cell / nav.Columns) * n) + y;
                for (int d = 0; d < 4; d++)
                {
                    (int dx, int dy) = d switch { 0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1) };
                    for (int k = 1; k <= reach; k++)
                    {
                        if (Column(nav, gx + (k * dx), gy + (k * dy)) is not { } there)
                        {
                            break;
                        }

                        foreach (int other in nav.ColumnBoxes(there.Cell, there.X, there.Y))
                        {
                            if ((nav.Flags(other) & NavHullFlags.Walkable) == 0)
                            {
                                continue;
                            }

                            float fb = nav.FloorZ(other);
                            float rise = fb - fa;
                            if (Math.Abs(rise) > nav.JumpHeight || (k == 1 && Math.Abs(rise) <= nav.StepHeight))
                            {
                                continue;
                            }

                            NavHullBox to = nav.Box(other);
                            int top = fa >= fb ? from.ZLo : to.ZLo;
                            if (!Open(nav, (cell, x, y), from.ZLo, top) || !Open(nav, there, to.ZLo, top))
                            {
                                continue;
                            }

                            bool gap = true;
                            for (int j = 1; j < k && gap; j++)
                            {
                                gap = Column(nav, gx + (j * dx), gy + (j * dy)) is { } between && Open(nav, between, top, top)
                                    && !FloorNear(nav, between, Math.Min(fa, fb) - nav.StepHeight, Math.Max(fa, fb) + nav.StepHeight);
                            }

                            if (!gap)
                            {
                                continue;
                            }

                            seen.Add(box < other
                                ? new NavHullJump((uint)box, (uint)other, (byte)x, (byte)y, (byte)there.X, (byte)there.Y, rise, (Nav3dDirection)d, (byte)k)
                                : new NavHullJump((uint)other, (uint)box, (byte)there.X, (byte)there.Y, (byte)x, (byte)y, -rise, (Nav3dDirection)((d + 2) % 4), (byte)k));
                        }
                    }
                }
            }
        }
    }

    /// <summary>A level voxel column as a cell and the column within it, or null outside the grid or in a cell with no boxes.</summary>
    private static (int Cell, int X, int Y)? Column(NavHullReader nav, int gx, int gy)
    {
        int n = nav.CellVoxels;
        if (gx < 0 || gy < 0 || gx >= nav.Columns * n || gy >= nav.Rows * n)
        {
            return null;
        }

        int cell = ((gy / n) * nav.Columns) + (gx / n);
        (int first, int end) = nav.CellBoxes(cell);
        return end > first ? (cell, gx % n, gy % n) : null;
    }

    /// <summary>Whether every voxel of a column from one height to another is in a box open to the player.</summary>
    private static bool Open(NavHullReader nav, (int Cell, int X, int Y) column, int from, int to)
    {
        ReadOnlySpan<int> boxes = nav.ColumnBoxes(column.Cell, column.X, column.Y);
        for (int z = Math.Min(from, to); z <= Math.Max(from, to); z++)
        {
            bool free = false;
            foreach (int box in boxes)
            {
                NavHullBox b = nav.Box(box);
                if (z >= b.ZLo && z <= b.ZHi)
                {
                    free = (nav.Flags(box) & NavHullFlags.PlayerBlocked) == 0;
                    break;
                }
            }

            if (!free)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a column has a walkable floor within a height range.</summary>
    private static bool FloorNear(NavHullReader nav, (int Cell, int X, int Y) column, float lo, float hi)
    {
        foreach (int box in nav.ColumnBoxes(column.Cell, column.X, column.Y))
        {
            if ((nav.Flags(box) & NavHullFlags.Walkable) != 0 && nav.FloorZ(box) >= lo && nav.FloorZ(box) <= hi)
            {
                return true;
            }
        }

        return false;
    }
}
