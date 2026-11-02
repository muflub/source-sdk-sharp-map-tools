//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using SourceSharp.MapFormats.Bsp;
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

using SourceSharp.MapTools.Nav;
using SourceSharp.Tests.MapTools.Rooms;

using Xunit;

namespace SourceSharp.Tests.MapTools.Nav;

/// <summary>
/// The floor check: every place a player stands in a built navigation has
/// ground under it in the map's own collision, so a navigation whose solid
/// was larger than the map's is caught where it puts a floor over nothing.
/// </summary>
public sealed class NavFloorCheckTests(BspNavMap map) : IClassFixture<BspNavMap>
{
    /// <summary>The static prop's model in <see cref="BspNavMap"/>: the 16 x 16 x 32 box at (64, 176, 16).</summary>
    private const string Box = RoomPropHarness.BoxModel;

    private Nav3dReader Open() => Nav3dReader.Open(Nav3dWriter.Write(map.Build().Level));

    /// <summary>
    /// The hall's navigation, built from the map's own solids, has ground
    /// under every place a player stands: on the floor, the step, the ledge,
    /// the displacement and the prop's top. The answer is the same at any
    /// thread count.
    /// </summary>
    [Fact]
    public void EveryPlaceToStandInTheHallHasGroundUnderIt()
    {
        Nav3dReader nav = Open();
        NavFloorReport report = NavFloorCheck.Check(nav, map.Bsp, BspNavMap.Hull, null);

        Assert.Empty(report.Missing);
        Assert.Empty(report.Warnings);
        Assert.Equal(18, report.Depth);
        Assert.True(report.Standable > 300, $"{report.Standable} places to stand");
        Assert.Equal(report.Standable, Enumerable.Range(0, nav.LeafCount).Count(l => Nav3dWalk.IsStand(nav, Nav3dWalker.Player, l)));
        NavFloorReport four = NavFloorCheck.Check(nav, map.Bsp, BspNavMap.Hull, null, new NavFloorCheckOptions { MaxDegree = 4 });
        Assert.Equal((report.Standable, 0), (four.Standable, four.Missing.Count));
    }

    /// <summary>
    /// A navigation built with the prop as its 32-high hull, checked against
    /// a map whose prop is only a 4-high slab: the four places on the hull's
    /// top (z 48) have nothing within a step under the player's box, and are
    /// reported; looking 40 units down, the slab and the floor are found.
    /// </summary>
    [Fact]
    public void AFloorOnASolidTheMapDoesNotHaveIsReported()
    {
        Nav3dReader nav = Open();
        NavPiece[] slab = [NavPiece.Box(new Vec3(-8, -8, 0), new Vec3(8, 8, 4))];
        Func<string, IReadOnlyList<NavPiece>?> pieces = m => m == Box ? slab : null;

        NavFloorReport report = NavFloorCheck.Check(nav, map.Bsp, BspNavMap.Hull, pieces);

        Assert.Equal(4, report.Missing.Count);
        Assert.All(report.Missing, f =>
        {
            Assert.Equal(48f, f.Position.Z);
            Assert.InRange(f.Position.X, 48, 80);
            Assert.InRange(f.Position.Y, 160, 192);
            Assert.True(Nav3dWalk.IsStand(nav, Nav3dWalker.Player, f.Leaf));
        });
        Assert.Equal(report.Missing.Select(f => f.Leaf).Order(), report.Missing.Select(f => f.Leaf));
        Assert.Equal(report.Missing, NavFloorCheck.Check(nav, map.Bsp, BspNavMap.Hull, pieces, new NavFloorCheckOptions { MaxDegree = 4 }).Missing);
        Assert.Empty(NavFloorCheck.Check(nav, map.Bsp, BspNavMap.Hull, pieces, new NavFloorCheckOptions { Depth = 40 }).Missing);
    }

    /// <summary>
    /// The collision is the world's brushes of the agent's class (player clip
    /// for the player, not for an NPC), the displacement's surface, and each
    /// solid static prop's pieces placed, or its hull; a prop whose model the
    /// content lacks is left out with one warning.
    /// </summary>
    [Fact]
    public void TheCollisionIsTheClassesBrushesTheDisplacementAndTheProps()
    {
        NavPiece wedge = new(
            [new(-8, -8, 0), new(8, -8, 0), new(-8, 8, 0), new(8, 8, 0), new(8, -8, 32), new(8, 8, 32)],
            [0, 1, 3, 0, 3, 2, 0, 4, 1, 2, 3, 5, 1, 4, 5, 1, 5, 3, 0, 2, 5, 0, 5, 4]);
        (IReadOnlyList<NavBrush> player, IReadOnlyList<string> none) = NavFloorCheck.Collision(map.Bsp, BspNavMap.Hull, m => m == Box ? [wedge] : null, Nav3dClipClass.Player);
        (IReadOnlyList<NavBrush> npc, _) = NavFloorCheck.Collision(map.Bsp, BspNavMap.Hull, null, Nav3dClipClass.Npc);
        (IReadOnlyList<NavBrush> bare, IReadOnlyList<string> warnings) = NavFloorCheck.Collision(map.Bsp, null, null, Nav3dClipClass.Player);

        Assert.Empty(none);
        static bool Clip(NavBrush b) => (b.Contents & 0x10000) != 0 && b.MinX == 300 && b.MaxX == 364;
        Assert.Contains(player, Clip);
        Assert.DoesNotContain(npc, Clip);
        Assert.Contains(player, b => b.MinY >= 400 && b.MaxY <= 496 && Math.Abs(b.MaxZ - BspNavMap.PatchTop) < 0.01);

        // The wedge, placed at (64, 176, 16): its slope runs from 16 at x 56
        // to 48 at x 72, so the point a quarter along is under it and the
        // one above the low end's middle is not.
        static bool AtTheProp(NavBrush b) => b.MinX >= 55 && b.MaxX <= 73 && b.MinY >= 167 && b.MaxY <= 185 && b.MinZ >= 15;
        NavBrush placed = Assert.Single(player, AtTheProp);
        Assert.Equal((56d, 168d, 16d, 72d, 184d, 48d), (Math.Round(placed.MinX, 3), Math.Round(placed.MinY, 3), Math.Round(placed.MinZ, 3), Math.Round(placed.MaxX, 3), Math.Round(placed.MaxY, 3), Math.Round(placed.MaxZ, 3)));
        Assert.True(placed.Overlaps(new NavBox(59, 175, 16, 61, 177, 22)));
        Assert.False(placed.Overlaps(new NavBox(59, 175, 30, 61, 177, 40)));
        Assert.True(Assert.Single(npc, AtTheProp).IsAxial);

        Assert.DoesNotContain(bare, AtTheProp);
        Assert.Equal(["static prop model \"models/props_test/box.mdl\" has no collision the content gives, so its 1 solid prop is left out of the check."], warnings);
    }
}
