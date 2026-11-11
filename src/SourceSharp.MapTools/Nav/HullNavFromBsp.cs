//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Buffers.Binary;
using System.Runtime.InteropServices;

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

namespace SourceSharp.MapTools.Nav;

/// <summary>What <see cref="HullNavFromBsp.Build"/> made: the navigation, and what it could not read.</summary>
/// <param name="Level">The navigation.</param>
/// <param name="Warnings">One sentence for each thing left out: a prop whose model the content lacks, and the like.</param>
public sealed record HullNavResult(NavHullLevel Level, IReadOnlyList<string> Warnings);

/// <summary>
/// Builds a <c>.navhull</c> from a compiled map: the grid and the clearance
/// <see cref="LevelNavFromBsp"/> builds, kept for one body shape, so that
/// space whose answers agree merges into boxes.
/// </summary>
/// <remarks>
/// <para>
/// <b>From the clearance records.</b> Each cell is built exactly as for a
/// <c>.nav3d</c>, then each run of its columns is reduced to what a body
/// of a built width needs: the lowest ceiling it meets above a column's
/// centre. A record blocks a centred body of half-width <c>r</c> with its
/// feet at <c>z</c> exactly when <c>z + h</c> passes a corner's top for some
/// corner narrower than <c>r</c> less half a voxel, so the least such top is
/// the whole answer, for every height. An overhanging brush, which the
/// record names rather than states, becomes two limits per width: the
/// highest top that passes under it and the lowest feet that stand above it,
/// found with the reader's own overlap test (<see cref="OverhangLimits"/>),
/// so a fit at any origin, on a floor inside the voxel below or raised up a
/// stair, is the <c>.nav3d</c> reader's. A dynamic obstacle's corners give
/// the ceilings with that obstacle shut.
/// </para>
/// <para>
/// <b>Runs, then boxes.</b> Touching runs of a column with the same answers
/// join (the upper never has a floor of its own, so the lower's stands for
/// both). Then, cell by cell, a run is grown east over columns holding the
/// same run, and that strip north over rows of the same strips: a box. Every
/// voxel a <c>.nav3d</c> leaf holds is in exactly one box, so a point
/// question (a sight line, a rocket) is answered as before.
/// </para>
/// <para>
/// <b>Deterministic.</b> Cells are converted on the build's threads into
/// their own slots, keys are numbered in cell order as first met, and the
/// jump links are sorted, so the file is the same bytes at any thread count.
/// </para>
/// </remarks>
public static class HullNavFromBsp
{
    /// <summary>
    /// The widths built when the caller names none: TF2's player (48), and
    /// 32, the narrower body a game tests a drop with on 16-unit voxels.
    /// </summary>
    public static IReadOnlyList<float> DefaultWidths => [32f, 48f];

    /// <summary>Builds a compiled map's hull navigation, on the thread pool.</summary>
    /// <param name="bsp">The map.</param>
    /// <param name="binding">The map file's binding, written into the file.</param>
    /// <param name="modelBounds">A model's hull by path, or null to leave props out.</param>
    /// <param name="widths">The body widths to build ceilings for; null for <see cref="DefaultWidths"/>.</param>
    /// <param name="options">The grid; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the build between columns.</param>
    /// <returns>The navigation and its warnings.</returns>
    public static Task<HullNavResult> BuildAsync(
        BspData bsp,
        Nav3dBspBinding binding,
        Func<string, (Vec3 Mins, Vec3 Maxs)?>? modelBounds,
        IReadOnlyList<float>? widths = null,
        BspNavOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        return Task.Run(() => Build(bsp, binding, modelBounds, widths, options, cancellationToken), cancellationToken);
    }

    /// <summary>Builds a compiled map's hull navigation on the calling thread (and the options' pool threads).</summary>
    /// <param name="bsp">The map.</param>
    /// <param name="binding">The map file's binding, written into the file.</param>
    /// <param name="modelBounds">A model's hull by path, or null to leave props out.</param>
    /// <param name="widths">The body widths to build ceilings for, 1 to 16 of them; null for <see cref="DefaultWidths"/>.</param>
    /// <param name="options">The grid; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the build between columns.</param>
    /// <returns>The navigation and its warnings.</returns>
    /// <exception cref="NavBuildException">The map has no world, is too tall at this voxel, or the widths are not positive.</exception>
    public static HullNavResult Build(
        BspData bsp,
        Nav3dBspBinding binding,
        Func<string, (Vec3 Mins, Vec3 Maxs)?>? modelBounds,
        IReadOnlyList<float>? widths = null,
        BspNavOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        options ??= new BspNavOptions();
        float[] built = [.. (widths ?? DefaultWidths).Order().Distinct()];
        if (built.Length is < 1 or > NavHullFormat.MaxWidths || built.Any(w => !(w > 0) || !float.IsFinite(w)))
        {
            throw new NavBuildException($"a hull navigation is built for 1 to {NavHullFormat.MaxWidths} widths, each above 0.");
        }

        // The cells are at most 128 voxels across (the grid's own rule, which
        // Plan enforces), so a box's footprint corners fit its bytes.
        LevelNavFromBsp.BspGridPlan plan = LevelNavFromBsp.Plan(bsp, modelBounds, options);
        HullCell?[] cells = LevelNavFromBsp.BuildCells(plan, options, (c, cell) => Convert(c, cell, plan, built), cancellationToken);
        NavHullLevel level = Stitch(cells, plan, options, built, binding, cancellationToken);
        return new HullNavResult(level, plan.Geometry.Warnings);
    }

    /// <summary>
    /// What one run of voxels answers for the player: its flags, cost, floor,
    /// a ceiling per width, and the ceilings per width with each obstacle
    /// that lowers one shut. Its bytes are its identity: two runs merge, and
    /// two boxes share a key, exactly when their bytes are equal.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="records">The cell's records.</param>
    /// <param name="cellBrushes">The cell's overhanging brushes' places in <paramref name="overhang"/>.</param>
    /// <param name="overhang">The level's overhanging brushes.</param>
    /// <param name="voxelSize">The voxel's edge.</param>
    /// <param name="cx">The column's centre along x, as the reader works it out from the leaf's bounds.</param>
    /// <param name="cy">Along y.</param>
    /// <param name="widths">The built widths.</param>
    internal static byte[] RunKey(NavRun run, NavRecordTable records, int[] cellBrushes, IReadOnlyList<NavBrush> overhang, double voxelSize, double cx, double cy, float[] widths)
    {
        Nav3dLeafFlags leafFlags = run.Key.Flags;
        bool blocked = run.Key.PlayerRecord == NavRecordTable.BlockedIndex;
        NavHullFlags flags = ((leafFlags & Nav3dLeafFlags.Water) != 0 ? NavHullFlags.Water : 0)
            | ((leafFlags & Nav3dLeafFlags.Ladder) != 0 ? NavHullFlags.Ladder : 0)
            | ((leafFlags & Nav3dLeafFlags.GroundedPlayer) != 0 ? NavHullFlags.Grounded : 0)
            | ((leafFlags & Nav3dLeafFlags.WalkablePlayer) != 0 ? NavHullFlags.Walkable : 0)
            | (blocked ? NavHullFlags.PlayerBlocked : 0);
        float floor = (flags & NavHullFlags.Grounded) != 0 ? run.Key.PlayerFloorZ : 0;

        float[] open = new float[widths.Length];
        List<(uint Obstacle, float[] Ceilings)> shuts = [];
        List<(double[] Under, double[] Over)> overhangs = [];
        if (blocked)
        {
            Array.Fill(open, float.NegativeInfinity);
        }
        else
        {
            NavRecord record = NavRecord.Decode(records[run.Key.PlayerRecord]);
            double s = voxelSize;
            for (int w = 0; w < widths.Length; w++)
            {
                double reach = (widths[w] * 0.5) - (s * 0.5);
                float ceiling = float.PositiveInfinity;
                foreach (Nav3dCorner corner in record.Corners)
                {
                    if (reach > corner.Width + NavBrush.Epsilon)
                    {
                        ceiling = Math.Min(ceiling, corner.Top);
                    }
                }

                open[w] = ceiling;
            }

            // An overhanging brush: where a centred body stops meeting it
            // from below and from above, per width (OverhangLimits). A record
            // names every one within reach of the voxel, but a body meets
            // only those over its own footprint, and one the corners' ceiling
            // already stops a body short of adds nothing: a body whose top is
            // at most the ceiling (give or take the margin) is under the
            // brush too. Both are left out, which keeps the answer and lets
            // the columns beside a far slope share a key.
            Span<double> under = stackalloc double[widths.Length];
            Span<double> over = stackalloc double[widths.Length];
            foreach (int b in record.Brushes)
            {
                bool matters = false;
                for (int w = 0; w < widths.Length; w++)
                {
                    (under[w], over[w]) = OverhangLimits(overhang[cellBrushes[b]], cx, cy, widths[w] * 0.5);
                    matters |= under[w] < open[w] + NavBrush.Epsilon;
                }

                if (matters)
                {
                    overhangs.Add((under.ToArray(), over.ToArray()));
                }
            }

            foreach (IGrouping<int, Nav3dDynamicCorner> obstacle in record.Dynamics.GroupBy(d => d.Obstacle).OrderBy(g => g.Key))
            {
                float[] shut = [.. open];
                for (int w = 0; w < widths.Length; w++)
                {
                    double reach = (widths[w] * 0.5) - (s * 0.5);
                    foreach (Nav3dDynamicCorner d in obstacle)
                    {
                        if (reach > d.Corner.Width + NavBrush.Epsilon)
                        {
                            shut[w] = Math.Min(shut[w], d.Corner.Top);
                        }
                    }
                }

                if (!shut.AsSpan().SequenceEqual(open))
                {
                    shuts.Add(((uint)obstacle.Key, shut));
                }
            }
        }

        int stride = 4 + (4 * widths.Length);
        int overhangStride = 16 * widths.Length;
        byte[] bytes = new byte[8 + (4 * widths.Length) + 4 + (shuts.Count * stride) + 4 + (overhangs.Count * overhangStride)];
        Span<byte> k = bytes;
        BinaryPrimitives.WriteUInt16LittleEndian(k, (ushort)flags);
        BinaryPrimitives.WriteUInt16LittleEndian(k[2..], run.Key.Cost);
        BinaryPrimitives.WriteSingleLittleEndian(k[4..], floor);
        for (int w = 0; w < widths.Length; w++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(k[(8 + (w * 4))..], open[w]);
        }

        int at = 8 + (4 * widths.Length);
        BinaryPrimitives.WriteInt32LittleEndian(k[at..], shuts.Count);
        at += 4;
        foreach ((uint obstacle, float[] ceilings) in shuts)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(k[at..], obstacle);
            for (int w = 0; w < widths.Length; w++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(k[(at + 4 + (w * 4))..], ceilings[w]);
            }

            at += stride;
        }

        BinaryPrimitives.WriteInt32LittleEndian(k[at..], overhangs.Count);
        at += 4;
        foreach ((double[] under, double[] over) in overhangs)
        {
            for (int w = 0; w < widths.Length; w++)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(k[(at + (w * 16))..], under[w]);
                BinaryPrimitives.WriteDoubleLittleEndian(k[(at + (w * 16) + 8)..], over[w]);
            }

            at += overhangStride;
        }

        return bytes;
    }

    /// <summary>
    /// Where a body of half-width <paramref name="half"/>, centred on a column,
    /// stops overlapping a brush: the highest top that passes under it and
    /// the lowest feet that stand above it, by the reader's own test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="NavBrush.Overlaps"/> is a separating-axis test with a
    /// margin. For a box over a fixed footprint, each axis separates it
    /// exactly when its feet are above some height or its top below some
    /// height (the axis's z component decides which), so the box overlaps
    /// exactly when its feet are under one limit and its top over another.
    /// Each limit is found by bisection on the double line with that same
    /// test, the other end held clear of the brush, so the reader's answer
    /// at any feet and height is the test's.
    /// </para>
    /// <para>
    /// A footprint that never meets the brush gives an under-limit of
    /// positive infinity: every body passes.
    /// </para>
    /// </remarks>
    internal static (double Under, double Over) OverhangLimits(NavBrush brush, double cx, double cy, double half)
    {
        ArgumentNullException.ThrowIfNull(brush);
        double low = brush.MinZ - 1;
        double high = brush.MaxZ + 1;
        (double x0, double y0, double x1, double y1) = (cx - half, cy - half, cx + half, cy + half);
        if (!brush.Overlaps(new NavBox(x0, y0, low, x1, y1, high)))
        {
            return (double.PositiveInfinity, double.NegativeInfinity);
        }

        // The highest top that clears, feet below the brush.
        double clear = low;
        double meets = high;
        for (double m = Mid(clear, meets); m > clear && m < meets; m = Mid(clear, meets))
        {
            (clear, meets) = brush.Overlaps(new NavBox(x0, y0, low, x1, y1, m)) ? (clear, m) : (m, meets);
        }

        double under = clear;

        // The lowest feet that clear, top above the brush.
        meets = low;
        clear = high;
        for (double m = Mid(meets, clear); m > meets && m < clear; m = Mid(meets, clear))
        {
            (meets, clear) = brush.Overlaps(new NavBox(x0, y0, m, x1, y1, high)) ? (m, clear) : (meets, m);
        }

        return (under, clear);

        static double Mid(double a, double b) => a + ((b - a) * 0.5);
    }

    /// <summary>Whether an upper run continues a lower one's box: the same answers but for the floor, which only the lower's bottom has.</summary>
    internal static bool Continues(byte[] lower, byte[] upper)
    {
        const ushort FloorFlags = (ushort)(NavHullFlags.Grounded | NavHullFlags.Walkable);
        if (lower.Length != upper.Length || (BinaryPrimitives.ReadUInt16LittleEndian(upper) & FloorFlags) != 0)
        {
            return false;
        }

        return (BinaryPrimitives.ReadUInt16LittleEndian(lower) & ~FloorFlags) == BinaryPrimitives.ReadUInt16LittleEndian(upper)
            && lower.AsSpan(2, 2).SequenceEqual(upper.AsSpan(2, 2))
            && lower.AsSpan(8).SequenceEqual(upper.AsSpan(8));
    }

    /// <summary>One cell's boxes, each naming one of the cell's own keys.</summary>
    internal sealed record HullCell(NavHullBox[] Boxes, List<byte[]> Keys);

    /// <summary>A cell's runs reduced to keys, joined up their columns, and merged across them into boxes.</summary>
    private static HullCell Convert(int c, LevelNavFromBsp.CellBuild cell, LevelNavFromBsp.BspGridPlan plan, float[] widths)
    {
        int n = plan.N;
        // The column's centre as the reader works it out from the leaf's
        // bounds (single-precision corners), so a brush test lines up.
        Vec3 origin = plan.Origin;
        double s = (float)plan.Voxel;
        int baseX = (c % plan.Columns) * n;
        int baseY = (c / plan.Columns) * n;
        double Centre(float at, int voxel)
        {
            double lo = at + (voxel * s);
            return ((float)lo + (double)(float)(lo + s)) * 0.5;
        }

        Dictionary<byte[], int> index = new(new ByteContent());
        List<byte[]> keys = [];
        List<HullRun>[] columns = new List<HullRun>[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                List<HullRun> runs = [];
                foreach (NavRun run in cell.Columns.Column(x, y))
                {
                    byte[] key = RunKey(run, cell.Records, cell.Brushes, plan.Overhang, s, Centre(origin.X, baseX + x), Centre(origin.Y, baseY + y), widths);
                    if (runs.Count > 0 && runs[^1].ZLo + runs[^1].Height == run.ZLo && Continues(keys[runs[^1].Key], key))
                    {
                        runs[^1] = runs[^1] with { Height = runs[^1].Height + run.Height };
                        continue;
                    }

                    if (!index.TryGetValue(key, out int number))
                    {
                        number = keys.Count;
                        index.Add(key, number);
                        keys.Add(key);
                    }

                    runs.Add(new HullRun(run.ZLo, run.Height, number));
                }

                columns[(y * n) + x] = runs;
            }
        }

        return new HullCell(Boxes(columns, n), keys);
    }

    /// <summary>
    /// A cell's column runs merged into boxes: in row then column order, each
    /// run not yet taken grows east over columns with the same run, and that
    /// strip north over rows whose same columns all hold it.
    /// </summary>
    internal static NavHullBox[] Boxes(List<HullRun>[] columns, int n)
    {
        bool[][] taken = [.. columns.Select(c => new bool[c.Count])];
        int Find(int x, int y, HullRun run)
        {
            List<HullRun> column = columns[(y * n) + x];
            for (int i = 0; i < column.Count; i++)
            {
                if (column[i] == run && !taken[(y * n) + x][i])
                {
                    return i;
                }
            }

            return -1;
        }

        List<NavHullBox> boxes = [];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                List<HullRun> column = columns[(y * n) + x];
                for (int i = 0; i < column.Count; i++)
                {
                    if (taken[(y * n) + x][i])
                    {
                        continue;
                    }

                    HullRun run = column[i];
                    int x1 = x + 1;
                    while (x1 < n && Find(x1, y, run) >= 0)
                    {
                        x1++;
                    }

                    int y1 = y + 1;
                    while (y1 < n && Enumerable.Range(x, x1 - x).All(xx => Find(xx, y1, run) >= 0))
                    {
                        y1++;
                    }

                    for (int yy = y; yy < y1; yy++)
                    {
                        for (int xx = x; xx < x1; xx++)
                        {
                            taken[(yy * n) + xx][Find(xx, yy, run)] = true;
                        }
                    }

                    boxes.Add(new NavHullBox((byte)x, (byte)y, (byte)x1, (byte)y1, (ushort)run.ZLo, (ushort)run.Height, (uint)run.Key));
                }
            }
        }

        return [.. boxes];
    }

    /// <summary>One column's run of one key, before boxes.</summary>
    internal readonly record struct HullRun(int ZLo, int Height, int Key);

    /// <summary>The cells' boxes in cell order, keys numbered as first met, the level's jump links found.</summary>
    private static NavHullLevel Stitch(
        HullCell?[] cells, LevelNavFromBsp.BspGridPlan plan, BspNavOptions options, float[] widths, Nav3dBspBinding binding, CancellationToken cancellationToken)
    {
        Dictionary<byte[], uint> index = new(new ByteContent());
        List<NavHullKey> keys = [];
        List<NavHullShut> shuts = [];
        List<NavHullOverhang> overhangs = [];
        uint[] starts = new uint[cells.Length + 1];
        List<NavHullBox> boxes = [];
        for (int c = 0; c < cells.Length; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            starts[c] = (uint)boxes.Count;
            if (cells[c] is not { } cell)
            {
                continue;
            }

            uint[] map = new uint[cell.Keys.Count];
            for (int k = 0; k < cell.Keys.Count; k++)
            {
                byte[] bytes = cell.Keys[k];
                if (!index.TryGetValue(bytes, out uint number))
                {
                    number = (uint)keys.Count;
                    index.Add(bytes, number);
                    keys.Add(KeyOf(bytes, widths.Length, shuts, overhangs));
                }

                map[k] = number;
            }

            foreach (NavHullBox box in cell.Boxes)
            {
                boxes.Add(box with { Key = map[box.Key] });
            }
        }

        starts[^1] = (uint)boxes.Count;
        options.Timer?.Mark("boxes");

        NavHullLevel unlinked = new()
        {
            VoxelSize = (float)plan.Voxel,
            CellVoxels = plan.N,
            Columns = plan.Columns,
            Rows = plan.Rows,
            Origin = plan.Origin,
            ColumnVoxels = plan.Nz,
            FloorNormalZ = plan.Settings.FloorNormalZ,
            StepHeight = plan.Settings.StepHeight,
            JumpHeight = plan.Settings.JumpHeight,
            JumpDistance = plan.Settings.JumpDistance,
            Widths = widths,
            CellStarts = starts,
            Boxes = [.. boxes],
            Keys = keys,
            Shuts = shuts,
            Overhangs = overhangs,
            Obstacles = LevelNavFromBsp.Obstacles(plan.Geometry, plan.Origin, options.CellSize, plan.Columns, plan.Rows),
            Bsp = binding,
        };

        // The jump links are found over the boxes as a reader lays them out
        // (each column's boxes low to high), so the level is read once
        // without them.
        IReadOnlyList<NavHullJump> jumps = NavHullJumps.Find(NavHullReader.Open(NavHullWriter.Write(unlinked)), options.MaxDegree, cancellationToken);
        options.Timer?.Mark("jumps");
        return new NavHullLevel
        {
            VoxelSize = unlinked.VoxelSize, CellVoxels = unlinked.CellVoxels, Columns = unlinked.Columns, Rows = unlinked.Rows, Origin = unlinked.Origin,
            ColumnVoxels = unlinked.ColumnVoxels, FloorNormalZ = unlinked.FloorNormalZ, StepHeight = unlinked.StepHeight, JumpHeight = unlinked.JumpHeight,
            JumpDistance = unlinked.JumpDistance, Widths = unlinked.Widths, CellStarts = unlinked.CellStarts, Boxes = unlinked.Boxes, Keys = unlinked.Keys,
            Shuts = unlinked.Shuts, Overhangs = unlinked.Overhangs, Obstacles = unlinked.Obstacles, Bsp = unlinked.Bsp, Jumps = jumps,
        };
    }

    /// <summary>A key's bytes as a file key, its shut records appended to the level's.</summary>
    private static NavHullKey KeyOf(byte[] bytes, int widths, List<NavHullShut> shuts, List<NavHullOverhang> overhangs)
    {
        ReadOnlySpan<byte> k = bytes;
        float[] open = new float[widths];
        for (int w = 0; w < widths; w++)
        {
            open[w] = BinaryPrimitives.ReadSingleLittleEndian(k[(8 + (w * 4))..]);
        }

        int at = 8 + (4 * widths);
        int count = BinaryPrimitives.ReadInt32LittleEndian(k[at..]);
        at += 4;
        uint first = (uint)shuts.Count;
        for (int i = 0; i < count; i++)
        {
            float[] ceilings = new float[widths];
            for (int w = 0; w < widths; w++)
            {
                ceilings[w] = BinaryPrimitives.ReadSingleLittleEndian(k[(at + 4 + (w * 4))..]);
            }

            shuts.Add(new NavHullShut(BinaryPrimitives.ReadUInt32LittleEndian(k[at..]), ceilings));
            at += 4 + (4 * widths);
        }

        int overhangCount = BinaryPrimitives.ReadInt32LittleEndian(k[at..]);
        at += 4;
        uint overhangFirst = (uint)overhangs.Count;
        for (int i = 0; i < overhangCount; i++)
        {
            double[] under = new double[widths];
            double[] over = new double[widths];
            for (int w = 0; w < widths; w++)
            {
                under[w] = BinaryPrimitives.ReadDoubleLittleEndian(k[(at + (w * 16))..]);
                over[w] = BinaryPrimitives.ReadDoubleLittleEndian(k[(at + (w * 16) + 8)..]);
            }

            overhangs.Add(new NavHullOverhang(under, over));
            at += 16 * widths;
        }

        return new NavHullKey((NavHullFlags)BinaryPrimitives.ReadUInt16LittleEndian(k), BinaryPrimitives.ReadUInt16LittleEndian(k[2..]),
            BinaryPrimitives.ReadSingleLittleEndian(k[4..]), open, first, (uint)count, overhangFirst, (uint)overhangCount);
    }

    /// <summary>Compares byte arrays by content.</summary>
    private sealed class ByteContent : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x is null ? y is null : y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj)
        {
            HashCode hash = new();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
