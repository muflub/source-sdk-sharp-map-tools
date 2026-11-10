//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;

using SourceSharp.MapFormats.Nav;

using Xunit;

namespace SourceSharp.Tests.MapFormats.Nav;

/// <summary>The brush's corners from its planes, and the exact overlap test on every family of axis.</summary>
public sealed class NavBrushTests
{
    private static NavBrush Wedge()
    {
        // A triangular prism along y: z ≤ x, x ≤ 10, z ≥ 0, 0 ≤ y ≤ 10.
        float r = MathF.Sqrt(0.5f);
        return NavBrush.FromPlanes([
            (new Vec3(-r, 0, r), 0f), (new Vec3(1, 0, 0), 10f), (new Vec3(0, 0, -1), 0f),
            (new Vec3(0, 1, 0), 10f), (new Vec3(0, -1, 0), 0f)], 1)!;
    }

    [Fact]
    public void ABrushsCornersComeFromItsPlanes()
    {
        NavBrush wedge = Wedge();
        Assert.Equal(6, wedge.VertexCount);
        Assert.Equal(5, wedge.PlaneCount);
        Assert.Equal((0.0, 0.0, 0.0, 10.0, 10.0, 10.0), (wedge.MinX, wedge.MinY, wedge.MinZ, wedge.MaxX, wedge.MaxY, wedge.MaxZ), new ToleranceComparer());
        Assert.Equal(1, wedge.Contents);
    }

    [Fact]
    public void PlanesThatBoundNothingMakeNoBrush()
    {
        Assert.Null(NavBrush.FromPlanes([(new Vec3(1, 0, 0), 0f), (new Vec3(-1, 0, 0), -5f)], 1));
        Assert.Null(NavBrush.FromPlanes([(new Vec3(0, 0, 1), 0f)], 1));
        Assert.Throws<ArgumentException>(() => NavBrush.Box(new Vec3(0, 0, 0), new Vec3(0, 1, 1), 1));
    }

    [Theory]
    [InlineData(6, 1, 0, 8, 9, 2, true)]     // inside
    [InlineData(10, 1, 0, 12, 9, 2, false)]  // touching the back face
    [InlineData(1, 1, 3, 3, 9, 5, false)]    // above the slope, clear of it by its plane
    [InlineData(4, 1, 3, 6, 9, 5, true)]     // straddling the slope
    [InlineData(2, 11, 0, 4, 12, 1, false)]  // beside it along y
    public void TheOverlapTestIsExact(double x0, double y0, double z0, double x1, double y1, double z1, bool overlaps) =>
        Assert.Equal(overlaps, Wedge().Overlaps(new NavBox(x0, y0, z0, x1, y1, z1)));

    [Fact]
    public void ABoxSeparatedOnlyAcrossAnEdgeDoesNotOverlap()
    {
        // The tetrahedron x, y, z ≥ 0, x + y + z ≤ 10, and a box beyond its
        // edge from (10, 0, 0) to (0, 10, 0): x + y ≥ 11 over the box, so they
        // are apart, but no face plane and no box axis says so; only the
        // edge's direction crossed with z does.
        float third = 1f / MathF.Sqrt(3f);
        NavBrush tetra = NavBrush.FromPlanes([
            (new Vec3(-1, 0, 0), 0f), (new Vec3(0, -1, 0), 0f), (new Vec3(0, 0, -1), 0f),
            (new Vec3(third, third, third), 10f * third)], 1)!;
        NavBox box = new(5.5, 5.5, -5, 7, 7, 5);
        Assert.False(tetra.Overlaps(box));
        Assert.True(tetra.Overlaps(box with { MinX = 4, MinY = 4 }));
    }

    [Fact]
    public void EdgeAxesCarryTheBrushsExtentAlongThem()
    {
        // The tetrahedron x, y, z ≥ 0, x + y + z ≤ 10: its edge from
        // (10, 0, 0) to (0, 10, 0) crossed with z is the diagonal (1, 1, 0),
        // along which the brush runs from 0 (the corner at the origin) to
        // 10 / √2 (that edge).
        float third = 1f / MathF.Sqrt(3f);
        NavBrush tetra = NavBrush.FromPlanes([
            (new Vec3(-1, 0, 0), 0f), (new Vec3(0, -1, 0), 0f), (new Vec3(0, 0, -1), 0f),
            (new Vec3(third, third, third), 10f * third)], 1)!;
        double r = Math.Sqrt(0.5);
        List<(double X, double Y, double Z, double Lo, double Hi)> axes = [.. Enumerable.Range(0, tetra.EdgeAxisCount).Select(tetra.EdgeAxis)];

        Assert.All(axes, a => Assert.Equal(1.0, Math.Sqrt((a.X * a.X) + (a.Y * a.Y) + (a.Z * a.Z)), 6));
        Assert.All(axes, a => Assert.True(a.Lo <= a.Hi));
        (double X, double Y, double Z, double Lo, double Hi) diagonal = Assert.Single(axes.Where(a => Math.Abs(Math.Abs(a.X) - r) < 1e-6 && Math.Abs(a.X - a.Y) < 1e-6 && Math.Abs(a.Z) < 1e-6).Take(1));
        double sign = Math.Sign(diagonal.X);
        Assert.Equal(sign > 0 ? 0 : -10 * r, diagonal.Lo, 4);
        Assert.Equal(sign > 0 ? 10 * r : 0, diagonal.Hi, 4);
    }

    [Fact]
    public void ABoxIsAxialAndItsBoundsDecideAlone()
    {
        NavBrush box = NavBrush.Box(new Vec3(0, 0, 0), new Vec3(10, 10, 10), 1);
        Assert.True(box.IsAxial);
        Assert.False(Wedge().IsAxial);
        Assert.True(box.Overlaps(new NavBox(9, 9, 9, 11, 11, 11)));
        Assert.False(box.Overlaps(new NavBox(10, 0, 0, 11, 1, 1)));
    }

    [Fact]
    public void ABrushTurnsExactly()
    {
        NavBrush box = NavBrush.Box(new Vec3(10, 20, 0), new Vec3(30, 40, 5), 1).Turned(1, 100);
        Assert.Equal((60.0, 10.0, 80.0, 30.0), (box.MinX, box.MinY, box.MaxX, box.MaxY));
        Assert.Equal((0.0, 1.0, 0.0, 30.0), box.Plane(0));
        Assert.Equal((-1.0, 0.0, 0.0, -60.0), box.Plane(2));
        NavBrush back = box.Turned(3, 100);
        Assert.Equal((10.0, 20.0, 30.0, 40.0), (back.MinX, back.MinY, back.MaxX, back.MaxY));
    }

    /// <summary>
    /// The growth threshold agrees with testing the grown box directly, for
    /// every growth the navigation uses, on a box brush, the wedge and an
    /// overhang: the grown box overlaps exactly past the threshold.
    /// </summary>
    [Fact]
    public void TheGrowthThresholdIsWhereTheGrownBoxStartsToOverlap()
    {
        float r = MathF.Sqrt(0.5f);
        NavBrush overhang = NavBrush.FromPlanes([
            (new Vec3(1, 0, 0), 20f), (new Vec3(-1, 0, 0), 0f), (new Vec3(0, 1, 0), 10f), (new Vec3(0, -1, 0), 0f),
            (new Vec3(0, 0, 1), 30f), (new Vec3(r, 0, -r), -10f * r)], 1)!;
        NavBrush[] brushes = [NavBrush.Box(new Vec3(4, 4, 0), new Vec3(8, 8, 6), 1), Wedge(), overhang];
        NavGrowth[] growths = [NavGrowth.Sideways, NavGrowth.Upward, NavGrowth.Downward, new(0.5, 0, 0, 2, 1, 0)];
        NavBox[] boxes = [new(-6, 1, 7, -4, 3, 8), new(12, 1, 2, 13, 2, 3), new(1, 1, 11, 2, 2, 12), new(5, 5, 7, 6, 6, 8)];
        foreach (NavBrush brush in brushes)
        {
            foreach (NavGrowth g in growths)
            {
                foreach (NavBox box in boxes)
                {
                    double t = brush.GrowthThreshold(box, g, out _);
                    NavBox Grown(double by) => new(
                        box.MinX - (g.MinX * by), box.MinY - (g.MinY * by), box.MinZ - (g.MinZ * by),
                        box.MaxX + (g.MaxX * by), box.MaxY + (g.MaxY * by), box.MaxZ + (g.MaxZ * by));
                    if (double.IsPositiveInfinity(t))
                    {
                        Assert.False(brush.Overlaps(Grown(1000)), $"{g} {box}");
                        continue;
                    }

                    if (t > 0)
                    {
                        Assert.False(brush.Overlaps(Grown(t - 1e-6)), $"{g} {box} below {t}");
                    }

                    Assert.True(brush.Overlaps(Grown(Math.Max(0, t) + 1e-6)), $"{g} {box} above {t}");
                }
            }
        }
    }

    /// <summary>
    /// Sideways growth takes its own path (each axis's rate kept on the
    /// brush): to the bit, threshold and contact, it is the general path's
    /// answer, for slanted brushes in every quarter turn and boxes all round
    /// them, below, beside, above and inside, including the cases where an
    /// axis keeps them apart at every width.
    /// </summary>
    [Fact]
    public void SidewaysGrowthGivesTheGeneralAnswerToTheBit()
    {
        float r = MathF.Sqrt(0.5f);
        NavBrush overhang = NavBrush.FromPlanes([
            (new Vec3(1, 0, 0), 20f), (new Vec3(-1, 0, 0), 0f), (new Vec3(0, 1, 0), 10f), (new Vec3(0, -1, 0), 0f),
            (new Vec3(0, 0, 1), 30f), (new Vec3(r, 0, -r), -10f * r)], 1)!;
        float k = 1f / MathF.Sqrt(3f);
        NavBrush corner = NavBrush.FromPlanes([
            (new Vec3(-1, 0, 0), 0f), (new Vec3(0, -1, 0), 0f), (new Vec3(0, 0, -1), 0f), (new Vec3(k, k, k), 12f * k)], 1)!;
        int finite = 0;
        int infinite = 0;
        foreach (NavBrush shape in (NavBrush[])[Wedge(), overhang, corner])
        {
            for (int turn = 0; turn < 4; turn++)
            {
                NavBrush brush = shape.Turned(turn, 20).Translated(new Vec3(0.37f, -0.21f, 0.13f));
                for (double x = -12; x <= 32; x += 3.5)
                {
                    for (double y = -12; y <= 32; y += 4.25)
                    {
                        for (double z = -8; z <= 36; z += 5.5)
                        {
                            NavBox box = new(x, y, z, x + 2, y + 2, z + 1.5);
                            double fast = brush.GrowthThreshold(box, NavGrowth.Sideways, out double fastContact);
                            double general = brush.AnyGrowthThreshold(box, NavGrowth.Sideways, out double generalContact);
                            Assert.True(
                                BitConverter.DoubleToInt64Bits(fast) == BitConverter.DoubleToInt64Bits(general)
                                    && BitConverter.DoubleToInt64Bits(fastContact) == BitConverter.DoubleToInt64Bits(generalContact),
                                $"turn {turn}, box {box}: sideways ({fast}, {fastContact}), general ({general}, {generalContact})");
                            _ = double.IsPositiveInfinity(fast) ? infinite++ : finite++;
                        }
                    }
                }
            }
        }

        Assert.True(finite > 1000 && infinite > 1000, $"{finite} finite and {infinite} infinite thresholds compared");
    }

    /// <summary>
    /// A column prepared once (<see cref="NavBrush.PrepareSidewaysColumn"/>)
    /// answers each of its boxes with the bits the sideways growth test gives
    /// that box: for slanted, overhanging and cornered brushes in every turn,
    /// a box, and a prism standing on a slope, over columns beside, under,
    /// over and inside them, at heights below, through and above, footprints
    /// on the brushes' own coordinates included (where a gap is exactly zero).
    /// </summary>
    [Fact]
    public void APreparedColumnGivesEachBoxsSidewaysAnswerToTheBit()
    {
        float r = MathF.Sqrt(0.5f);
        NavBrush overhang = NavBrush.FromPlanes([
            (new Vec3(1, 0, 0), 20f), (new Vec3(-1, 0, 0), 0f), (new Vec3(0, 1, 0), 10f), (new Vec3(0, -1, 0), 0f),
            (new Vec3(0, 0, 1), 30f), (new Vec3(r, 0, -r), -10f * r)], 1)!;
        float k = 1f / MathF.Sqrt(3f);
        NavBrush corner = NavBrush.FromPlanes([
            (new Vec3(-1, 0, 0), 0f), (new Vec3(0, -1, 0), 0f), (new Vec3(0, 0, -1), 0f), (new Vec3(k, k, k), 12f * k)], 1)!;
        Vec3 slope = new Vec3(0.3f, -0.2f, 1f).Normalise().Normalised;
        NavBrush prism = NavBrush.FromPlanes([
            (slope, Vec3.Dot(slope, new Vec3(4, 4, 8))), (new Vec3(0, 0, -1), 0f),
            (new Vec3(1, 0, 0), 12f), (new Vec3(-1, 0, 0), -2f), (new Vec3(0, 1, 0), 14f), (new Vec3(-0.6f, -0.8f, 0), -1f)], 1)!;
        NavBrush box = NavBrush.Box(new Vec3(2, 3, 4), new Vec3(9, 8, 16), 1);
        int finite = 0;
        int infinite = 0;
        int compared = 0;
        foreach (NavBrush shape in (NavBrush[])[Wedge(), overhang, corner, prism, box])
        {
            for (int turn = 0; turn < 4; turn++)
            {
                NavBrush brush = shape.Turned(turn, 20).Translated(new Vec3(0.37f, -0.21f, 0.13f));
                double[] prepared = new double[brush.SidewaysColumnLength];
                double[] xs = [-12, -4.5, brush.MinX - 2, brush.MinX, 3, 9.25, brush.MaxX, 26];
                double[] ys = [-12, brush.MinY - 2, brush.MinY, 2.5, 7.75, brush.MaxY, 30];
                foreach (double x in xs)
                {
                    foreach (double y in ys)
                    {
                        brush.PrepareSidewaysColumn(x, y, x + 2, y + 2, prepared);
                        for (double z = -8; z <= 36; z += 1.375)
                        {
                            foreach (double top in (double[])[z + 1.5, Math.Max(z + 1.5, brush.MaxZ) + 1])
                            {
                                NavBox voxel = new(x, y, z, x + 2, y + 2, top);
                                double expected = brush.GrowthThreshold(voxel, NavGrowth.Sideways, out _);
                                double actual = brush.SidewaysThreshold(prepared, z, top);
                                Assert.True(
                                    BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
                                    $"turn {turn}, box {voxel}: growth test {expected}, prepared column {actual}");
                                _ = double.IsPositiveInfinity(expected) ? infinite++ : finite++;
                                compared++;
                            }
                        }
                    }
                }
            }
        }

        Assert.True(finite > 2000 && infinite > 2000, $"{finite} finite and {infinite} infinite thresholds compared");
    }

    /// <summary>A prepared column needs room for all of its numbers.</summary>
    [Fact]
    public void APreparedColumnTooShortIsRefused()
    {
        NavBrush wedge = Wedge();
        Assert.True(wedge.SidewaysColumnLength > 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => wedge.PrepareSidewaysColumn(0, 0, 1, 1, new double[wedge.SidewaysColumnLength - 1]));
    }

    [Fact]
    public void AFlatTopMetFromAboveIsContactOneAndABrushTheBoxNeverReachesIsInfinite()
    {
        NavBrush floor = NavBrush.Box(new Vec3(0, 0, 0), new Vec3(10, 10, 5), 1);
        Assert.Equal(3 + NavBrush.Epsilon, floor.GrowthThreshold(new NavBox(1, 1, 8, 2, 2, 8), NavGrowth.Downward, out double contact), 12);
        Assert.Equal(1.0, contact);
        Assert.Equal(double.PositiveInfinity, floor.GrowthThreshold(new NavBox(20, 1, 8, 21, 2, 9), NavGrowth.Downward, out _));
        // A box already inside overlaps at t = 0: its threshold is where shrinking its top would free it, below zero.
        Assert.Equal(-2 + NavBrush.Epsilon, floor.GrowthThreshold(new NavBox(1, 1, 1, 2, 2, 2), NavGrowth.Upward, out _), 12);

        // Down a 45° slope the contact is its normal.
        NavBrush wedge = Wedge();
        _ = wedge.GrowthThreshold(new NavBox(5, 1, 8, 6, 2, 9), NavGrowth.Downward, out double slope);
        Assert.Equal(MathF.Sqrt(0.5f), slope, 5);
    }

    [Fact]
    public void AnOverhangIsASlopedFaceTurnedDown()
    {
        float r = MathF.Sqrt(0.5f);
        Assert.False(NavBrush.Box(new Vec3(0, 0, 0), new Vec3(1, 1, 1), 1).IsOverhang);
        Assert.False(Wedge().IsOverhang);
        Assert.True(NavBrush.FromPlanes([
            (new Vec3(1, 0, 0), 20f), (new Vec3(-1, 0, 0), 0f), (new Vec3(0, 1, 0), 10f), (new Vec3(0, -1, 0), 0f),
            (new Vec3(0, 0, 1), 30f), (new Vec3(r, 0, -r), -10f * r)], 1)!.IsOverhang);
    }

    [Fact]
    public void ABrushMovesAndRoundTripsThroughFloatPlanes()
    {
        NavBrush wedge = Wedge();
        NavBrush moved = wedge.Translated(new Vec3(100, -50, 8));
        Assert.Equal((100.0, -50.0, 8.0, 110.0, -40.0, 18.0), (moved.MinX, moved.MinY, moved.MinZ, moved.MaxX, moved.MaxY, moved.MaxZ), new ToleranceComparer());
        Assert.Equal(wedge.Overlaps(new NavBox(4, 1, 3, 6, 9, 5)), moved.Overlaps(new NavBox(104, -49, 11, 106, -41, 13)));
        float[] floats = wedge.PlaneFloats();
        Assert.Equal(20, floats.Length);
        NavBrush again = NavBrush.FromPlaneFloats(floats, 7)!;
        Assert.Equal((wedge.MinX, wedge.MaxZ, 7), (again.MinX, again.MaxZ, again.Contents));
        Assert.Throws<ArgumentException>(() => NavBrush.FromPlaneFloats([1, 0, 0], 1));
        Assert.Null(NavBrush.FromPlaneFloats([1, 0, 0, 1, -1, 0, 0, 5], 1));
    }

    private sealed class ToleranceComparer : IEqualityComparer<(double, double, double, double, double, double)>
    {
        public bool Equals((double, double, double, double, double, double) a, (double, double, double, double, double, double) b) =>
            Math.Abs(a.Item1 - b.Item1) < 1e-4 && Math.Abs(a.Item2 - b.Item2) < 1e-4 && Math.Abs(a.Item3 - b.Item3) < 1e-4
            && Math.Abs(a.Item4 - b.Item4) < 1e-4 && Math.Abs(a.Item5 - b.Item5) < 1e-4 && Math.Abs(a.Item6 - b.Item6) < 1e-4;

        public int GetHashCode((double, double, double, double, double, double) obj) => 0;
    }
}
