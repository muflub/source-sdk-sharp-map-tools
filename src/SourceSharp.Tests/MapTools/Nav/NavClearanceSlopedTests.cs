//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// The sloped solids' corners, which the builder computes only when they can
/// still lower a voxel's staircase: the floor it orders them by is never
/// above a corner's width, and the staircase it builds is the one listing and
/// sorting every candidate gives.
/// </summary>
public sealed class NavClearanceSlopedTests
{
    private const double E = NavBrush.Epsilon;

    private const int Player = Nav3dFormat.PlayerSolidMask;

    private const int Npc = Nav3dFormat.NpcSolidMask;

    // Solid for one class only.
    private const int PlayerClip = 0x10000;

    private const int MonsterClip = 0x20000;

    private static readonly NavRegion Cell = new(0, 0, 0, 16, 16, 16, 16);

    /// <summary>A ramp rising at 45° along +x: bounding box x 64 to 192, y −64 to 320, z 16 to 144, under <c>z ≤ x − 48</c>.</summary>
    private static NavBrush Ramp(int contents = 1)
    {
        float r = MathF.Sqrt(0.5f);
        return NavBrush.FromPlanes(
        [
            (new Vec3(0, 0, -1), -16f),
            (new Vec3(1, 0, 0), 192f),
            (new Vec3(-r, 0, r), -48f * r),
            (new Vec3(0, 1, 0), 320f),
            (new Vec3(0, -1, 0), 64f),
        ], contents)!;
    }

    /// <summary>A pyramid frustum: 64 square at the base, 16 at the top, 48 tall, its base's low corner at the origin.</summary>
    private static NavBrush Frustum(int contents = 1)
    {
        float r = 1f / MathF.Sqrt(1f + (0.5f * 0.5f));
        float k = 0.5f * r;
        return NavBrush.FromPlanes(
        [
            (new Vec3(0, 0, -1), 0f),
            (new Vec3(0, 0, 1), 48f),
            (new Vec3(r, 0, k), 64f * r),
            (new Vec3(-r, 0, k), 0f),
            (new Vec3(0, r, k), 64f * r),
            (new Vec3(0, -r, k), 0f),
        ], contents)!;
    }

    /// <summary>The column of voxel (x, y) in <see cref="Cell"/>, as the builder computes it.</summary>
    private static (double X0, double Y0, double X1, double Y1) Column(int x, int y)
    {
        NavBox v = Cell.Voxel(x, y, 0);
        return (v.MinX, v.MinY, v.MaxX, v.MaxY);
    }

    /// <summary>The staircase a voxel's static corners make when every candidate is listed and sorted by width, then bottom.</summary>
    private static List<Nav3dCorner> ReferenceStair(IReadOnlyList<NavBrush> brushes, in NavBox voxel, int bit)
    {
        List<(double Width, double Bottom)> candidates = [];
        foreach (NavBrush brush in brushes)
        {
            int mask = ((brush.Contents & Player) != 0 ? 1 : 0) | ((brush.Contents & Npc) != 0 ? 2 : 0);
            if ((mask & bit) == 0 || (brush.IsOverhang && !brush.IsAxial) || !(brush.MaxZ > voxel.MinZ + E))
            {
                continue;
            }

            if (brush.IsAxial)
            {
                candidates.Add((Math.Max(Math.Max(voxel.MinX - brush.MaxX, brush.MinX - voxel.MaxX), Math.Max(voxel.MinY - brush.MaxY, brush.MinY - voxel.MaxY)),
                    brush.MinZ));
            }
            else if (NavClearanceBuilder.SlopedCorner(brush, voxel) is { } c)
            {
                candidates.Add((c.Width, c.Bottom));
            }
        }

        candidates.Sort((a, b) => a.Width != b.Width ? a.Width.CompareTo(b.Width) : a.Bottom.CompareTo(b.Bottom));
        List<Nav3dCorner> stair = [];
        foreach ((double width, double bottom) in candidates)
        {
            Nav3dCorner corner = NavClearanceBuilder.Normalise(width, bottom, voxel.MaxZ);
            if (stair.Count > 0 && !(corner.Top < stair[^1].Top))
            {
                continue;
            }

            if (stair.Count > 0 && stair[^1].Width == corner.Width)
            {
                stair[^1] = corner;
                continue;
            }

            stair.Add(corner);
        }

        return stair;
    }

    /// <summary>A record's static corners.</summary>
    private static List<Nav3dCorner> Corners(byte[] record)
    {
        List<Nav3dCorner> corners = [];
        for (int i = 0; i < Nav3dClearance.StaticCount(record); i++)
        {
            corners.Add(Nav3dClearance.Corner(record, i));
        }

        return corners;
    }

    /// <summary>Every voxel and class of a build: the record's corners are the reference staircase's.</summary>
    /// <returns>How many of them the sloped brushes change: the reference staircase without them differs.</returns>
    private static int AssertStairsAreTheReference(IReadOnlyList<NavBrush> brushes)
    {
        NavGrid grid = NavClearanceBuilder.Build(new NavGeometry { Brushes = [.. brushes] }, Cell, NavSettings.Default);
        NavBrush[] axial = [.. brushes.Where(b => b.IsAxial)];
        int sloped = 0;
        for (int z = 0; z < Cell.SizeZ; z++)
        {
            for (int y = 0; y < Cell.SizeY; y++)
            {
                for (int x = 0; x < Cell.SizeX; x++)
                {
                    NavBox voxel = Cell.Voxel(x, y, z);
                    foreach (Nav3dClipClass clipClass in (Nav3dClipClass[])[Nav3dClipClass.Player, Nav3dClipClass.Npc])
                    {
                        List<Nav3dCorner> expected = ReferenceStair(brushes, voxel, clipClass == Nav3dClipClass.Npc ? 2 : 1);
                        List<Nav3dCorner> actual = Corners(grid.Record(x, y, z, clipClass));
                        Assert.True(expected.SequenceEqual(actual),
                            $"voxel {x} {y} {z} {clipClass}: expected [{string.Join(", ", expected)}], built [{string.Join(", ", actual)}]");
                        sloped += expected.SequenceEqual(ReferenceStair(axial, voxel, clipClass == Nav3dClipClass.Npc ? 2 : 1)) ? 0 : 1;
                    }
                }
            }
        }

        return sloped;
    }

    /// <summary>
    /// The floor is never above the corner's width: for two sloped shapes in
    /// every quarter turn, seen from every column of a cell around them and
    /// from voxels below, inside and above them.
    /// </summary>
    [Fact]
    public void TheWidthFloorIsNeverAboveTheSlopedCorner()
    {
        int compared = 0;
        foreach (NavBrush shape in (NavBrush[])[Ramp(), Frustum().Translated(new Vec3(72, 40, 20))])
        {
            for (int turn = 0; turn < 4; turn++)
            {
                NavBrush brush = shape.Turned(turn, 256);
                for (int y = 0; y < Cell.SizeY; y++)
                {
                    for (int x = 0; x < Cell.SizeX; x++)
                    {
                        (double x0, double y0, double x1, double y1) = Column(x, y);
                        double floor = NavClearanceBuilder.SlopedWidthFloor(brush, x0, y0, x1, y1);
                        for (int z = 0; z < Cell.SizeZ; z += 3)
                        {
                            if (NavClearanceBuilder.SlopedCorner(brush, Cell.Voxel(x, y, z)) is { } corner)
                            {
                                Assert.True(floor <= corner.Width, $"turn {turn}, voxel {x} {y} {z}: floor {floor} above width {corner.Width}");
                                compared++;
                            }
                        }
                    }
                }
            }
        }

        Assert.True(compared > 1000, $"only {compared} corners compared");
    }

    /// <summary>
    /// Where the bounding box's side is what the growing box meets last, the
    /// floor is the width itself, but for the last bits of rounding (another
    /// axis through the same corner may give way a hair later): the bound is
    /// tight, so a brush whose floor is just past the staircase's end is one
    /// whose corner is just past it too.
    /// </summary>
    [Fact]
    public void TheWidthFloorIsTheWidthWhereTheBoxSideDecides()
    {
        // Column 0 is 48 units short of the ramp's low x edge, beside its
        // foot: the slope plane gives way at a gap of 32, the box side at 48.
        NavBrush ramp = Ramp();
        (double x0, double y0, double x1, double y1) = Column(0, 0);
        double floor = NavClearanceBuilder.SlopedWidthFloor(ramp, x0, y0, x1, y1);
        NavClearanceBuilder.Candidate? corner = NavClearanceBuilder.SlopedCorner(ramp, Cell.Voxel(0, 0, 0));
        Assert.NotNull(corner);
        Assert.True(Math.Abs(floor - 48) < 1e-4, $"floor {floor}");
        Assert.InRange(corner.Value.Width - floor, 0, 1e-9);
    }

    /// <summary>
    /// A wall that blocks every height ends the staircase at its width. A
    /// ramp above the voxels whose floor is just inside that width is
    /// computed and adds its corner; one just outside is never needed, and
    /// the records are the reference staircase's either way.
    /// </summary>
    [Fact]
    public void ASlopedBrushJustInsideTheEndAddsItsCornerAndOneJustOutsideIsLeftOut()
    {
        // Column 0's gap to the wall is 400 − 16 = 384. The ramp's own gap is
        // 48 plus its shift: 383.5 inside the wall's, 384.5 outside it.
        NavBrush wall = NavBrush.Box(new Vec3(400, -64, -64), new Vec3(416, 320, 512), 1);
        NavBrush inside = Ramp().Translated(new Vec3(335.5f, 0, 300));
        NavBrush outside = Ramp().Translated(new Vec3(336.5f, 0, 100));
        (double x0, double y0, double x1, double y1) = Column(0, 0);
        Assert.True(NavClearanceBuilder.SlopedWidthFloor(inside, x0, y0, x1, y1) < 384);
        Assert.True(NavClearanceBuilder.SlopedWidthFloor(outside, x0, y0, x1, y1) > 384);

        AssertStairsAreTheReference([wall, inside, outside]);
        NavGrid grid = NavClearanceBuilder.Build(new NavGeometry { Brushes = [wall, inside, outside] }, Cell, NavSettings.Default);
        List<Nav3dCorner> stair = Corners(grid.Record(0, 0, 0, Nav3dClipClass.Player));
        Assert.Equal(2, stair.Count);
        Assert.InRange(stair[0].Width, 383.49f, 383.5f);
        Assert.Equal(316, stair[0].Top);
        Assert.Equal(new Nav3dCorner(384, float.NegativeInfinity), stair[1]);

        // Without the wall the outside ramp's corner is in the staircase: it
        // was left out above only because the wall ends the staircase first.
        NavGrid open = NavClearanceBuilder.Build(new NavGeometry { Brushes = [inside, outside] }, Cell, NavSettings.Default);
        Assert.Contains(Corners(open.Record(0, 0, 0, Nav3dClipClass.Player)), c => c.Width is > 384.49f and <= 384.5f && c.Top == 116);
    }

    /// <summary>
    /// A cell crowded with sloped brushes at many places and heights, solid
    /// for one class or both, among axial pillars and a floor: every voxel's
    /// record, for both classes, is the staircase listing and sorting every
    /// candidate gives.
    /// </summary>
    [Fact]
    public void ACrowdedCellsRecordsAreTheReferenceStaircases()
    {
        List<NavBrush> brushes =
        [
            NavBrush.Box(new Vec3(-64, -64, -64), new Vec3(320, 320, 16), 1),
            NavBrush.Box(new Vec3(100, 20, -64), new Vec3(116, 36, 180), PlayerClip),
            NavBrush.Box(new Vec3(30, 150, 90), new Vec3(70, 230, 110), MonsterClip),
            NavBrush.Box(new Vec3(-200, -200, -64), new Vec3(-180, 500, 400), 1),
        ];
        int[] contents = [1, PlayerClip, MonsterClip];
        for (int i = 0; i < 18; i++)
        {
            Vec3 offset = new((float)(((i * 53) % 300) - 120), (float)(((i * 97) % 280) - 140), (float)(((i * 41) % 200) - 20));
            NavBrush shape = i % 2 == 0 ? Ramp(contents[i % 3]) : Frustum(contents[i % 3]);
            brushes.Add(shape.Turned(i % 4, 256).Translated(offset));
        }

        int sloped = AssertStairsAreTheReference(brushes);
        Assert.True(sloped > 100, $"only {sloped} records carry a sloped brush's corner");
    }
}
